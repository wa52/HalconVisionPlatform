using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HalconVision.Domain;
using HalconVision.Persistence;
using Microsoft.Win32;

namespace HalconVision.App;

public partial class RunArchiveWindow : Window
{
    private readonly WorkflowWorkspaceViewModel _workspace;
    private readonly CancellationTokenSource _closing = new();
    private IReadOnlyList<ArchiveHistoryEntry> _entries = [];
    private int _page;
    private bool _busy;
    private readonly Guid? _projectId;
    public RunArchiveWindow(Window owner, WorkflowWorkspaceViewModel workspace)
    {
        InitializeComponent(); Owner = owner; _workspace = workspace;
        _projectId = workspace.Workspace?.Project.Id;
        var settings = workspace.Workspace?.Project.RunArchive ?? new();
        Folder.Text = settings.Directory; EnabledArchive.IsChecked = settings.Enabled;
        SaveSettings.IsEnabled = workspace.HasProject;
        EnabledArchive.IsEnabled = workspace.HasProject;
        if (!workspace.HasProject) SaveSettings.ToolTip = EnabledArchive.ToolTip = "请先打开项目，再配置自动归档；历史查询仍可使用。";
        Previous.IsEnabled = Next.IsEnabled = false;
        ResultDetail.Text = FlowDetail.Text = "选择历史记录查看详情；不影响当前检测。";
        Closed += (_, _) => _closing.Cancel();
        Loaded += async (_, _) => { if (Directory.Exists(Folder.Text)) await LoadAsync(); };
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择归档目录" };
        if (dialog.ShowDialog(this) == true) { Folder.Text = dialog.FolderName; _page = 0; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_workspace.Workspace?.Project.Id != _projectId) throw new InvalidOperationException("当前项目已切换，请关闭后重新打开归档窗口再设置。");
            _workspace.ConfigureRunArchive(new(EnabledArchive.IsChecked == true, Folder.Text.Trim()));
            Feedback.Text = "归档设置及当前流程已保存到项目。完整检测包含成功、失败和取消；上游示教不归档。";
        }
        catch (Exception error) { Feedback.Text = "设置未保存：" + error.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { _page = 0; await LoadAsync(); }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (_busy) return; _page = Math.Max(0, _page - 1); await LoadAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (_busy) return; _page++; await LoadAsync(); }
    private async Task LoadAsync()
    {
        if (_busy) return; _busy = true;
        Refresh.IsEnabled = Previous.IsEnabled = Next.IsEnabled = false; Folder.IsEnabled = false;
        Feedback.Text = "正在读取并校验历史归档…";
        try
        {
            var directory = Folder.Text.Trim(); var pageNumber = _page;
            var result = await Task.Run(() => WorkflowArchiveHistory.ReadPage(directory, pageNumber, _closing.Token));
            if (_closing.IsCancellationRequested || Folder.Text.Trim() != directory || _page != pageNumber) return;
            _entries = result.Entries; ApplyFilter();
            Previous.IsEnabled = _page > 0; Next.IsEnabled = result.HasNext;
            PageLabel.Text = $"第 {_page + 1} 页";
            Feedback.Text = $"本页 {_entries.Count} 条，{_entries.Count(e => e.Run is null)} 条无法读取。按写入时间倒序；本页筛选不搜索其他页。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing.IsCancellationRequested) { _entries = []; ApplyFilter(); Feedback.Text = "读取失败：" + error.Message; } }
        finally { _busy = false; if (!_closing.IsCancellationRequested) { Refresh.IsEnabled = true; Folder.IsEnabled = true; } }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (History is not null) ApplyFilter(); }
    private void Folder_Changed(object sender, TextChangedEventArgs e)
    {
        if (History is null) return;
        _page = 0; _entries = []; ApplyFilter(); Previous.IsEnabled = Next.IsEnabled = false; PageLabel.Text = "";
        Feedback.Text = "目录已更改，请刷新历史记录。";
    }
    private void ApplyFilter()
    {
        var query = Search.Text.Trim();
        var rows = _entries.Where(r => $"{r.Project} {r.Flow} {r.Verdict} {r.RunId}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        History.ItemsSource = rows; Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultDetail.Text = FlowDetail.Text = "选择历史记录查看详情；不影响当前检测。";
    }
    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (History.SelectedItem is not ArchiveHistoryEntry entry) return;
        if (entry.Run is not { } run) { ResultDetail.Text = "该记录无法校验，不能作为有效检测结果。\n" + entry.Error + "\n" + entry.File; FlowDetail.Text = "—"; return; }
        string Display(object value)
        {
            var text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            return text.Length > 100000 ? text[..100000] + "\n显示已截断；完整数据保留在归档文件中。" : text;
        }
        ResultDetail.Text = $"项目：{run.ProjectName}   流程：{run.Flow.Name}\n历史结论：{entry.Verdict}   完成时间：{entry.Completed}\n只读归档：{entry.File}\n" + Display(new { run.RunId, run.StartedAtUtc, run.EndedAtUtc, run.Status, run.Inspection, run.NodeResults, run.Errors, run.Logs });
        FlowDetail.Text = "执行时流程快照；原图/模型未打包，不可直接复现。\n" + Display(run.Flow);
    }
}
