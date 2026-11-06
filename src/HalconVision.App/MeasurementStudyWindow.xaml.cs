using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using HalconVision.Application;
using Microsoft.Win32;

namespace HalconVision.App;

public partial class MeasurementStudyWindow : Window
{
    public static string ScopeNotice => MeasurementStudy.Limitation;
    private readonly WorkflowWorkspaceViewModel _workspace;
    private MeasurementStudy? _study;
    private readonly ObservableCollection<MeasurementObservation> _rows = [];
    private bool _exported = true;
    public sealed record FieldChoice(Guid NodeId, string NodeName, string PortId, string PortName, string Unit)
    { public string Label => $"{NodeName} · {PortName} ({Unit})"; }

    public MeasurementStudyWindow(Window owner, WorkflowWorkspaceViewModel workspace)
    {
        InitializeComponent(); Owner = owner; _workspace = workspace;
        SamplesGrid.ItemsSource = _rows;
        RefreshFields(); ExportButton.IsEnabled = false;
        Closing += ConfirmClose;
    }
    private void RefreshFields()
    {
        var fields = _workspace.SelectedFlow is { } flow
            ? MeasurementStudy.AvailableFields(flow.Document, _workspace.NodeCatalog)
                .Select(f => new FieldChoice(f.Node.Id, f.Node.DisplayName, f.Port.Id, f.Port.Name, f.Port.Unit!)).ToArray() : [];
        FieldSelector.ItemsSource = fields; FieldSelector.SelectedIndex = fields.Length == 0 ? -1 : 0;
        CaptureButton.IsEnabled = fields.Length > 0;
        if (fields.Length == 0) Feedback.Text = "当前流程没有像素或毫米长度输出。请添加测量节点后新建验证记录。";
    }
    private MeasurementStudy CreateStudy()
    {
        if (FieldSelector.SelectedItem is not FieldChoice field || _workspace.SelectedFlow is not { } flow)
            throw new InvalidOperationException("请选择当前流程的测量字段。");
        if (!double.TryParse(ReferenceValue.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var reference) || !double.IsFinite(reference))
            throw new InvalidOperationException("参考值必须是有限数值，单位与所选字段一致。");
        return new(new(flow.Id, field.NodeId, field.NodeName, field.PortId, field.PortName, field.Unit,
            InspectionPlan.ConfigurationIdentity(flow.Document), ReferenceName.Text.Trim(), reference));
    }
    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_workspace.IsRunning || _workspace.IsContinuousRunning) throw new InvalidOperationException("请等待本轮完成并停止连续运行后记录。");
            if (_workspace.LastRun is not { } run || _workspace.SelectedFlow is not { } flow) throw new InvalidOperationException("请先在主窗口运行当前流程。");
            var study = _study ?? CreateStudy();
            var sample = study.Capture(flow.Document, run, SampleName.Text);
            _study = study; _rows.Add(sample); SettingsPanel.IsEnabled = false; _exported = false;
            ExportButton.IsEnabled = true; EmptyHint.Visibility = Visibility.Collapsed;
            var s = study.Statistics;
            Counts.Text = $"已记录 {s.Total} 轮 · 有效 {s.Valid} · 失测 {s.Missing} · {study.Settings.Unit}";
            string N(double? n) => n?.ToString("G9", CultureInfo.CurrentCulture) ?? "—";
            StatisticsText.Text = $"均值 {N(s.Mean)}    偏差 {N(s.Bias)}    标准差 {N(s.SampleStandardDeviation)}\n最小 {N(s.Minimum)}    最大 {N(s.Maximum)}    极差 {N(s.Range)}    最大绝对误差 {N(s.MaximumAbsoluteError)}";
            Feedback.Text = sample.Value.HasValue ? "已记录。重新取像运行后再记录下一轮；至少两次有效测量才计算样本标准差。" : "本轮失测已计入总数，不进入有效值统计：" + sample.Reason;
            Feedback.Foreground = (System.Windows.Media.Brush)FindResource("WorkbenchTextBrush");
            SamplesGrid.ScrollIntoView(sample);
        }
        catch (Exception ex) { Feedback.Text = ex.Message; Feedback.Foreground = System.Windows.Media.Brushes.Orange; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_study is null) return;
        var dialog = new SaveFileDialog { Title = "导出测量验证报告", Filter = "完整报告 (*.json)|*.json|表格报告 (*.csv)|*.csv", FileName = "测量验证", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { SaveReport(dialog.FileName); }
        catch (Exception ex) { Feedback.Text = "导出失败，记录仍保留：" + ex.Message; Feedback.Foreground = System.Windows.Media.Brushes.Orange; }
    }
    private void SaveReport(string path)
    {
        if (_study is null) throw new InvalidOperationException("尚无验证记录。");
        // A separate report file; never write to project/image/model paths.
        if (Path.GetExtension(path).ToLowerInvariant() is not (".json" or ".csv")) throw new InvalidOperationException("报告只允许保存为 .json 或 .csv。");
        var text = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? _study.ToCsv() : _study.ToJson();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(true)); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        _exported = true; Feedback.Text = "已导出报告：" + path;
        Feedback.Foreground = (System.Windows.Media.Brush)FindResource("WorkbenchTextBrush");
    }
    private bool MayDiscard() => _exported || MessageBox.Show(this, "验证记录尚未导出。继续会丢弃本窗口的记录，是否继续？", "测量验证", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!MayDiscard()) return;
        _study = null; _rows.Clear(); _exported = true; SettingsPanel.IsEnabled = true; ExportButton.IsEnabled = false;
        Counts.Text = "尚未记录"; StatisticsText.Text = "均值、偏差、标准差与极差将在记录后显示。";
        EmptyHint.Visibility = Visibility.Visible; Feedback.Text = "选择字段和参考值，再运行流程并记录。";
        Feedback.Foreground = (System.Windows.Media.Brush)FindResource("WorkbenchTextBrush");
        RefreshFields();
    }
    private void ConfirmClose(object? sender, CancelEventArgs e) { if (!MayDiscard()) e.Cancel = true; }
    internal bool TryCloseStudy() { Close(); return !IsVisible; }
}
