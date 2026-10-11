using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;
using Microsoft.Win32;

namespace HalconVision.App;

public partial class MainWindow : Window
{
    private readonly WorkflowWorkspaceViewModel _vm = new();
    private NodePortViewModel? _pendingOutput;
    private FlowNodeViewModel? _dragNode;
    private Point _dragOffset;
    private bool _dragging;
    private readonly HalconImagePresenter _imagePresenter = new();
    private GridLength _leftColumnWidth = new(260);
    private GridLength _rightColumnWidth = new(300);
    private GridLength _bottomRowHeight = new(270);
    private bool _leftVisible = true;
    private bool _rightVisible = true;
    private bool _bottomVisible = true;
    private string? _previewImagePath;
    private int _previewImageWidth;
    private int _previewImageHeight;
    private bool _panningPreview;
    private Point _lastPreviewPointer;
    private CancellationTokenSource? _continuousCancellation;

    public MainWindow()
    {
        InitializeComponent(); DataContext = _vm;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var halconRoot = Environment.GetEnvironmentVariable("HALCONROOT");
        var versionPath = string.IsNullOrWhiteSpace(halconRoot) ? null : Path.Combine(halconRoot, "version.txt");
        var version = versionPath is not null && File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "未检测到";
        EnvironmentText.Text = $"HALCON {version}  ·  {Environment.GetEnvironmentVariable("HALCONARCH") ?? "架构未知"}";
        _vm.Log("节点工作区就绪。创建或打开项目以开始编辑流程。");
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSaveBeforeSwitch()) return;
        var dialog = new SaveFileDialog
        {
            Title = "新建 HALCON 项目",
            Filter = "HALCON 项目文件|*.hvproj",
            DefaultExt = ".hvproj",
            AddExtension = true,
            FileName = "新建项目.hvproj",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var projectPath = Path.ChangeExtension(dialog.FileName, ".hvproj");
        var name = Path.GetFileNameWithoutExtension(projectPath);
        try { _vm.CreateProject(projectPath, name); _vm.Log($"已新建空白项目：{projectPath}"); }
        catch (Exception ex) { ShowError("创建项目失败", ex); }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSaveBeforeSwitch()) return;
        var dialog = new OpenFileDialog { Title = "打开 HALCON 项目", Filter = "HALCON 项目文件|*.hvproj;project.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _vm.OpenProject(dialog.FileName); _vm.Log($"项目已打开：{dialog.FileName}"); }
        catch (Exception ex) { ShowError("打开项目失败", ex); }
    }

    private bool ConfirmSaveBeforeSwitch()
    {
        if (!_vm.HasUnsavedFlows) return true;
        var result = MessageBox.Show(this, "当前流程有未保存的更改。是否保存后继续？", "未保存更改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) { _vm.SaveAll(); return true; }
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) { try { _vm.SaveAll(); _vm.Log("项目与流程已保存。"); } catch (Exception ex) { ShowError("保存失败", ex); } }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "HALCON 视觉检测平台\n可扩展节点式工程工作台\n图像读取、二值化和 Blob 分析通过 HALCON 24.11 执行。", "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ToggleLeft_Click(object sender, RoutedEventArgs e)
    {
        _leftVisible = !_leftVisible;
        if (_leftVisible) { LeftColumn.Width = _leftColumnWidth; LeftSplitterColumn.Width = new GridLength(5); }
        else { if (LeftColumn.Width.Value > 0) _leftColumnWidth = LeftColumn.Width; LeftColumn.Width = new GridLength(0); LeftSplitterColumn.Width = new GridLength(0); }
    }

    private void ToggleRight_Click(object sender, RoutedEventArgs e)
    {
        _rightVisible = !_rightVisible;
        if (_rightVisible) { RightColumn.Width = _rightColumnWidth; RightSplitterColumn.Width = new GridLength(5); }
        else { if (RightColumn.Width.Value > 0) _rightColumnWidth = RightColumn.Width; RightColumn.Width = new GridLength(0); RightSplitterColumn.Width = new GridLength(0); }
    }

    private void ToggleBottom_Click(object sender, RoutedEventArgs e)
    {
        _bottomVisible = !_bottomVisible;
        if (_bottomVisible) { BottomRow.Height = _bottomRowHeight; BottomSplitterRow.Height = new GridLength(5); }
        else { if (BottomRow.Height.Value > 0) _bottomRowHeight = BottomRow.Height; BottomRow.Height = new GridLength(0); BottomSplitterRow.Height = new GridLength(0); }
    }

    private void FitCanvas_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.SetZoom(0.85);
    private void OpenImageViewer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewImagePath) || !File.Exists(_previewImagePath))
        {
            _vm.Log("请先在图像读取节点中选择图像，再使用独立查看。");
            return;
        }
        new ImageViewerWindow(_previewImagePath) { Owner = this, WindowState = WindowState.Maximized }.Show();
    }
    private void Copy_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.CopySelection();
    private void Paste_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.PasteSelection();
    private void Delete_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.DeleteSelection();

    private void ProjectArchive_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasProject) { MessageBox.Show(this, "请先打开项目。", "项目管理", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var choice = MessageBox.Show(this, "选择“是”导出当前项目，选择“否”从归档导入。", "项目归档", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                var save = new SaveFileDialog { Filter = "HALCON 项目归档|*.zip", FileName = $"{_vm.ProjectName}.zip" };
                if (save.ShowDialog(this) == true) { _vm.ExportProject(save.FileName); _vm.Log($"项目已导出：{save.FileName}"); }
            }
            else if (choice == MessageBoxResult.No)
            {
                if (!ConfirmSaveBeforeSwitch()) return;
                var open = new OpenFileDialog { Filter = "HALCON 项目归档|*.zip" };
                if (open.ShowDialog(this) != true) return;
                var folder = new OpenFolderDialog { Title = "选择导入项目的父目录" };
                if (folder.ShowDialog(this) != true) return;
                var name = Prompt("导入项目", "项目名称", Path.GetFileNameWithoutExtension(open.FileName)); if (name is null) return;
                _vm.ImportProject(open.FileName, folder.FolderName, name); _vm.Log("项目归档已导入。");
            }
        }
        catch (Exception ex) { ShowError("项目归档失败", ex); }
    }

    private void NewFlow_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("新建流程", "流程名称", "新流程"); if (name is null) return;
        try { _vm.CreateFlow(name); _vm.Log($"已创建流程：{name}"); } catch (Exception ex) { ShowError("创建流程失败", ex); }
    }

    private void ImportFlow_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "流程文件|*.flow.json;*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _vm.ImportFlow(dialog.FileName); _vm.Log("流程文件已导入。"); } catch (Exception ex) { ShowError("导入流程失败", ex); }
    }

    private void ExportFlow_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFlow is null) return;
        var dialog = new SaveFileDialog { Filter = "流程文件|*.flow.json", FileName = $"{_vm.SelectedFlow.Name}.flow.json" };
        if (dialog.ShowDialog(this) == true) try { _vm.ExportFlow(_vm.SelectedFlow, dialog.FileName); _vm.Log("流程已导出。"); } catch (Exception ex) { ShowError("导出流程失败", ex); }
    }

    private void AddNode_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFlow is null || NodeCatalogList.SelectedItem is not NodeDefinition definition) return;
        _vm.SelectedFlow.AddNode(definition); _vm.Log($"已添加节点：{definition.DisplayName}");
    }
    private void NodeCatalog_DoubleClick(object sender, MouseButtonEventArgs e) => AddNode_Click(sender, e);
    private void Undo_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.Redo();

    private void Node_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject original && FindParent<ButtonBase>(original) is not null) return;
        if (sender is not FrameworkElement { DataContext: FlowNodeViewModel node } element || _vm.SelectedFlow is null) return;
        _vm.SelectedFlow.SelectNode(node.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        DisplaySelectedNodePreview();
        var canvas = FindParent<Canvas>(element); if (canvas is null) return;
        _dragNode = node; _dragOffset = e.GetPosition(canvas) - new Vector((int)node.X, (int)node.Y); _dragging = false;
        element.CaptureMouse(); e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement element) return;
        var canvas = FindParent<Canvas>(element); if (canvas is null) return;
        var point = e.GetPosition(canvas);
        if (!_dragging) { _vm.SelectedFlow?.BeginMove(); _dragging = true; }
        _vm.SelectedFlow?.MoveNode(_dragNode.Id, point.X - _dragOffset.X, point.Y - _dragOffset.Y);
    }

    private void Node_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element) element.ReleaseMouseCapture();
        _dragNode = null; _dragging = false;
    }

    private void Port_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodePortViewModel port } || _vm.SelectedFlow is null) return;
        if (port.Direction == PortDirection.Output) { _pendingOutput = port; _vm.Log($"已选择输出端口：{port.Name}，请点击目标输入端口。"); return; }
        if (_pendingOutput is null) { _vm.Log("请先选择一个输出端口。"); return; }
        if (_vm.SelectedFlow.Connect(_pendingOutput.NodeId, _pendingOutput.Id, port.NodeId, port.Id, out var error)) _vm.Log($"连线已创建：{_pendingOutput.Name} → {port.Name}");
        else _vm.Log($"连线未创建：{error}");
        _pendingOutput = null;
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm.SelectedFlow is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _vm.SelectedFlow.AdjustZoom(e.Delta > 0 ? 0.1 : -0.1); e.Handled = true;
    }

    private void Parameter_LostFocus(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: NodeParameterViewModel parameter }) parameter.Commit(); }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasProject || _vm.SelectedFlow is null) { MessageBox.Show(this, "请先创建或打开项目。", "运行流程", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_vm.IsRunning) return;
        try
        {
            var flow = _vm.SelectedFlow;
            var progress = new Progress<NodeExecutionResult>(result => { flow.ApplyNodeResult(result); _vm.Log($"节点 {result.Status}: {result.NodeId}"); });
            var runFlow = _vm.PrepareNextImageFlow(out var imagePath, out var hasSequence, out var sequenceEnded);
            if (sequenceEnded || runFlow is null)
            {
                _vm.Log("文件夹图像已处理完，序列已重置；再次单次运行将从第一张开始。");
                return;
            }
            if (hasSequence) _vm.Log($"单次运行：{Path.GetFileName(imagePath)}");
            var run = await _vm.RunSelectedFlowAsync(progress, runFlow);
            _vm.SetExecutionRun(run);
            _vm.Log($"流程执行结束：{run.Status}，耗时 {(run.EndedAtUtc - run.StartedAtUtc).TotalMilliseconds:F1} ms。");
            if (run.Status == WorkflowRunStatus.Success) DisplayLastSuccessfulNode(flow);
            else _vm.ReturnImageToSequence(imagePath);
            if (run.Errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, run.Errors), "流程执行错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError("流程执行失败", ex); }
    }

    private async void ContinuousRun_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasProject || _vm.SelectedFlow is null) { MessageBox.Show(this, "请先创建或打开项目。", "连续运行", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_continuousCancellation is not null || _vm.IsRunning) return;
        _continuousCancellation = new CancellationTokenSource();
        try
        {
            var flow = _vm.SelectedFlow;
            var progress = new Progress<NodeExecutionResult>(result => { flow.ApplyNodeResult(result); _vm.Log($"节点 {result.Status}: {result.NodeId}"); });
            var first = true;
            while (!_continuousCancellation.IsCancellationRequested)
            {
                if (!ReferenceEquals(_vm.SelectedFlow, flow))
                {
                    _vm.Log("连续运行因切换流程而停止。");
                    break;
                }
                var runFlow = _vm.PrepareNextImageFlow(out var imagePath, out var hasSequence, out var sequenceEnded);
                if (sequenceEnded)
                {
                    if (!hasSequence) break;
                    _vm.Log("文件夹图像序列循环至第一张。");
                    continue;
                }
                if (runFlow is null) break;
                if (first && !hasSequence)
                {
                    MessageBox.Show(this, "连续运行需要在读取图像节点中选择一个图像文件夹。", "连续运行", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }
                first = false;
                _vm.Log($"连续运行：{Path.GetFileName(imagePath)}");
                var run = await _vm.RunSelectedFlowAsync(progress, runFlow);
                _vm.SetExecutionRun(run);
                if (run.Status == WorkflowRunStatus.Success)
                {
                    DisplayLastSuccessfulNode(flow);
                    continue;
                }
                _vm.ReturnImageToSequence(imagePath);
                if (run.Status != WorkflowRunStatus.Cancelled && run.Errors.Count > 0)
                    MessageBox.Show(this, string.Join(Environment.NewLine, run.Errors), "连续运行错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
            _vm.Log(_continuousCancellation.IsCancellationRequested ? "连续运行已停止。" : "连续运行结束。");
        }
        catch (Exception ex) { ShowError("连续运行失败", ex); }
        finally { _continuousCancellation.Dispose(); _continuousCancellation = null; }
    }

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _continuousCancellation?.Cancel();
        _vm.CancelRun();
    }

    private void DisplayLastSuccessfulNode(FlowTabViewModel flow)
    {
        var nodeResult = _vm.LastRun?.NodeResults.LastOrDefault(result => result.Status == NodeExecutionStatus.Success);
        if (nodeResult is null) return;
        flow.SelectNode(nodeResult.NodeId, false);
        DisplaySelectedNodePreview();
    }

    private void DisplaySelectedNodePreview()
    {
        var node = _vm.SelectedFlow?.SelectedNode;
        var run = _vm.LastRun;
        if (node is null || run is null || run.FlowId != _vm.SelectedFlow?.Id || run.RuntimeOutputs is null ||
            !run.RuntimeOutputs.TryGetValue(node.Id, out var runtime) ||
            runtime.Values.OfType<HObject>().FirstOrDefault() is not { } iconicObject)
        {
            PreviewEmptyText.Visibility = Visibility.Visible;
            if (node is not null && node.TypeId == HalconNodeProvider.ImageRead)
                PreviewEmptyText.Text = "图像节点运行后将在此显示图像";
            else PreviewEmptyText.Text = "此节点尚无可显示的图像结果";
            PreviewWindow.HalconWindow.ClearWindow();
            return;
        }

        var nodeResult = run.NodeResults.FirstOrDefault(result => result.NodeId == node.Id);
        if (nodeResult is null) return;
        var outputMetadata = nodeResult.Outputs.Values.FirstOrDefault(value => value.ValueKind == JsonValueKind.Object);
        var width = outputMetadata.ValueKind == JsonValueKind.Object && outputMetadata.TryGetProperty("width", out var widthValue) ? widthValue.GetInt32() : 0;
        var height = outputMetadata.ValueKind == JsonValueKind.Object && outputMetadata.TryGetProperty("height", out var heightValue) ? heightValue.GetInt32() : 0;
        if (iconicObject is HImage image && image.CountObj() > 1)
        {
            using var firstImage = image.SelectObj(1);
            firstImage.GetImageSize(out width, out height);
            _imagePresenter.Display(firstImage, PreviewWindow.HalconWindow, width, height, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        }
        else
        {
            if (width <= 0 || height <= 0)
            {
                if (iconicObject is HImage imageObject) imageObject.GetImageSize(out width, out height);
            }
            _imagePresenter.Display(iconicObject, PreviewWindow.HalconWindow, width, height, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        }
        _previewImageWidth = width; _previewImageHeight = height;
        _previewImagePath = null;
        PreviewEmptyText.Visibility = Visibility.Collapsed;
        _vm.Log($"预览节点结果：{node.DisplayName}");
    }

    private void PickImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not NodeParameterViewModel parameter) return;
        string? selectedPath;
        if (string.Equals(button.Content?.ToString(), "选文件夹…", StringComparison.Ordinal))
        {
            var folderDialog = new OpenFolderDialog { Title = "选择包含图像的文件夹", Multiselect = false };
            if (folderDialog.ShowDialog(this) != true) return;
            selectedPath = folderDialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 HALCON 输入图像",
                Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.jp2|所有文件|*.*",
                CheckFileExists = true,
                FileName = File.Exists(parameter.ValueText) ? parameter.ValueText : string.Empty
            };
            if (dialog.ShowDialog(this) != true) return;
            selectedPath = dialog.FileName;
        }

        selectedPath = Path.GetFullPath(selectedPath);
        parameter.ValueText = selectedPath;
        parameter.Commit();
        var previewPath = Directory.Exists(selectedPath)
            ? EnumerateImageFiles(selectedPath).FirstOrDefault()
            : selectedPath;
        if (previewPath is null)
        {
            _previewImagePath = null;
            _vm.Log("所选文件夹中没有支持的图像文件。");
            return;
        }

        _previewImagePath = previewPath;
        try
        {
            var dimensions = _imagePresenter.Display(_previewImagePath, PreviewWindow.HalconWindow);
            _previewImageWidth = dimensions.Width;
            _previewImageHeight = dimensions.Height;
            Dispatcher.BeginInvoke(new Action(() => ImageFit_Click(this, new RoutedEventArgs())));
        }
        catch (Exception ex) { _vm.Log($"HALCON 图像预览失败：{ex}"); }
        _vm.Log(Directory.Exists(selectedPath)
            ? $"已选择图像文件夹：{selectedPath}（预览第一张：{Path.GetFileName(previewPath)}）"
            : $"已选择图像：{selectedPath}");
    }

    private static IEnumerable<string> EnumerateImageFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".bmp" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".jp2")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private void ImageZoomIn_Click(object sender, RoutedEventArgs e) => _imagePresenter.Zoom(PreviewWindow.HalconWindow, 1.25);
    private void ImageZoomOut_Click(object sender, RoutedEventArgs e) => _imagePresenter.Zoom(PreviewWindow.HalconWindow, 0.8);
    private void ImageFit_Click(object sender, RoutedEventArgs e)
    {
        if (_previewImageWidth <= 0 || _previewImageHeight <= 0) return;
        _imagePresenter.Fit(PreviewWindow.HalconWindow, _previewImageWidth, _previewImageHeight, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
    }

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _imagePresenter.Zoom(PreviewWindow.HalconWindow, e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void PreviewHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _panningPreview = true;
        _lastPreviewPointer = e.GetPosition(PreviewHost);
        PreviewHost.CaptureMouse();
        PreviewHost.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panningPreview || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(PreviewHost);
        var delta = pointer - _lastPreviewPointer;
        _imagePresenter.Pan(PreviewWindow.HalconWindow, delta.X, delta.Y, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        _lastPreviewPointer = pointer;
        e.Handled = true;
    }

    private void PreviewHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panningPreview) return;
        _panningPreview = false;
        PreviewHost.ReleaseMouseCapture();
        PreviewHost.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void Preview_HMouseDown(object sender, HalconDotNet.HSmartWindowControlWPF.HMouseEventArgsWPF e) { }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isTextEditing = Keyboard.FocusedElement is TextBoxBase;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.S) { Save_Click(sender, e); e.Handled = true; }
            else if (isTextEditing) return;
            else if (e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.C) { _vm.SelectedFlow?.CopySelection(); e.Handled = true; }
            else if (e.Key == Key.V) { _vm.SelectedFlow?.PasteSelection(); e.Handled = true; }
        }
        else if (!isTextEditing && e.Key == Key.Delete) { _vm.SelectedFlow?.DeleteSelection(); e.Handled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmSaveBeforeSwitch()) { e.Cancel = true; return; }
        _continuousCancellation?.Cancel();
        _vm.CancelRun();
        _vm.Dispose();
    }

    private static string? Prompt(string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Width = 390, Height = 155, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Owner = global::System.Windows.Application.Current.MainWindow, Background = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = initial }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        string? answer = null; var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 72, Margin = new Thickness(0, 0, 6, 0) }; ok.Click += (_, _) => { answer = input.Text.Trim(); dialog.DialogResult = true; };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 72 }; buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); dialog.Content = panel;
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(answer) ? answer : null;
    }

    private void ShowError(string title, Exception ex) { _vm.Log($"{title}：{ex}"); MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current)) if (current is T parent) return parent;
        return null;
    }
}
