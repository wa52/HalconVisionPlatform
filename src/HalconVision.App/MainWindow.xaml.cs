using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShapeLine = System.Windows.Shapes.Line;
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
    private bool _detectionMode;
    private readonly Dictionary<UIElement, Visibility> _editorVisibility = [];

    private void DetectionMode_Changed(object sender, RoutedEventArgs e)
    {
        if (RuntimePanel is null) return;
        SetDetectionMode(DetectionModeToggle.IsChecked == true);
    }

    private void SetDetectionMode(bool enabled)
    {
        if (_detectionMode == enabled) return;
        FinishRoiDrawing();
        _detectionMode = enabled;
        if (enabled)
        {
            foreach (UIElement child in WorkspaceGrid.Children)
                if (child != RuntimePanel) { _editorVisibility[child] = child.Visibility; child.Visibility = Visibility.Collapsed; }
            Grid.SetRow(RuntimePanel, 0); Grid.SetRowSpan(RuntimePanel, 3);
            Grid.SetColumn(RuntimePanel, 0); Grid.SetColumnSpan(RuntimePanel, 5);
        }
        else
        {
            foreach (var (child, visibility) in _editorVisibility) child.Visibility = visibility;
            _editorVisibility.Clear();
            Grid.SetRow(RuntimePanel, 2); Grid.SetRowSpan(RuntimePanel, 1);
            Grid.SetColumn(RuntimePanel, 2); Grid.SetColumnSpan(RuntimePanel, 1);
        }
        DetectionViewSelector.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyDetectionView();
    }

    private void DetectionView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuntimeImagePanel is not null) ApplyDetectionView();
    }

    private void ApplyDetectionView()
    {
        var imageView = !_detectionMode || DetectionViewSelector.SelectedIndex == 0;
        RuntimeImagePanel.Visibility = imageView ? Visibility.Visible : Visibility.Collapsed;
        RuntimeResultsPanel.Visibility = !_detectionMode || !imageView ? Visibility.Visible : Visibility.Collapsed;
        RuntimeSplitter.Visibility = _detectionMode ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(RuntimeImagePanel, _detectionMode ? 3 : 1);
        Grid.SetColumn(RuntimeResultsPanel, _detectionMode ? 0 : 2);
        Grid.SetColumnSpan(RuntimeResultsPanel, _detectionMode ? 3 : 1);
        if (_detectionMode && !imageView)
            RuntimeTabs.SelectedIndex = DetectionViewSelector.SelectedIndex == 1 ? 1 : 0;
        Dispatcher.BeginInvoke(new Action(() => { if (!_closing && imageView) ImageFit_Click(this, new RoutedEventArgs()); }));
    }

    private void DisplayNode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayNodeSelector.SelectedItem is FlowNodeViewModel node && _vm.SelectedFlow is { } flow)
            flow.SelectNode(node.Id, false);
        if (PreviewWindow is not null && NodeOutputsGrid is not null) DisplaySelectedNodePreview();
    }
    private NodePortViewModel? _pendingOutput;
    private ButtonBase? _connectionCapture;
    private Canvas? _connectionCanvas;
    private ShapeLine? _connectionPreview;
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
    private NodeParameterViewModel? _roiParameter;
    private bool _isDrawingRoi;
    private Point _roiStart;
    private CancellationTokenSource? _continuousCancellation;
    private Task? _executionTask;
    private bool _closing;
    private bool _closeReady;
    private bool _switchingProject;
    private bool _panningCanvas;
    private Point _lastCanvasPointer;
    private ScrollViewer? _canvasScrollViewer;
    private readonly string? _startupProjectPath;

    public MainWindow(string? startupProjectPath = null)
    {
        _startupProjectPath = startupProjectPath;
        InitializeComponent(); DataContext = _vm;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var halconRoot = Environment.GetEnvironmentVariable("HALCONROOT");
        var versionPath = string.IsNullOrWhiteSpace(halconRoot) ? null : Path.Combine(halconRoot, "version.txt");
        var version = versionPath is not null && File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "未检测到";
        EnvironmentText.Text = $"HALCON {version}  ·  {Environment.GetEnvironmentVariable("HALCONARCH") ?? "架构未知"}";
        _vm.Log("节点工作区就绪。创建或打开项目以开始编辑流程。");
        if (!string.IsNullOrWhiteSpace(_startupProjectPath))
        {
            try { _vm.OpenProject(_startupProjectPath); _vm.Log($"已从项目文件打开：{_startupProjectPath}"); }
            catch (Exception ex) { ShowError("打开项目文件失败", ex); }
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _switchingProject) return;
        await PrepareProjectSwitchAsync();
        if (_closing) return;
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
        try { _vm.CreateProject(projectPath, name); ClearPreview(); _vm.Log($"已新建空白项目：{projectPath}"); }
        catch (Exception ex) { ShowError("创建项目失败", ex); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _switchingProject) return;
        await PrepareProjectSwitchAsync();
        if (_closing) return;
        if (!ConfirmSaveBeforeSwitch()) return;
        var dialog = new OpenFileDialog { Title = "打开 HALCON 项目", Filter = "HALCON 项目文件|*.hvproj;project.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _vm.OpenProject(dialog.FileName); ClearPreview(); _vm.Log($"项目已打开：{dialog.FileName}"); }
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
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var removed = _vm.SelectedFlow?.DeleteSelection() ?? 0;
        _vm.Log(removed > 0 ? $"已删除 {removed} 个节点及相关连线。" : "请先选中要删除的节点。");
    }

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
        _vm.LoadSelectedCameraDevice();
        element.Focus();
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
        _vm.SelectedFlow.SelectNode(port.NodeId, false);
        _vm.LoadSelectedCameraDevice();
        if (port.Direction == PortDirection.Output) { _pendingOutput = port; _vm.Log($"已选择输出端口：{port.Name}，请点击目标输入端口。"); return; }
        if (_pendingOutput is null) { _vm.Log("请先选择一个输出端口。"); return; }
        if (_vm.SelectedFlow.Connect(_pendingOutput.NodeId, _pendingOutput.Id, port.NodeId, port.Id, out var error)) _vm.Log($"连线已创建：{_pendingOutput.Name} → {port.Name}");
        else _vm.Log($"连线未创建：{error}");
        _pendingOutput = null;
    }

    private void Port_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ButtonBase { Tag: NodePortViewModel { Direction: PortDirection.Output } port } button) return;
        _pendingOutput = port;
        _connectionCanvas = FindWorkflowCanvas(button);
        if (_connectionCanvas is null) return;
        var start = button.TransformToAncestor(_connectionCanvas).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        _connectionPreview = new ShapeLine
        {
            X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y,
            Stroke = new SolidColorBrush(Color.FromRgb(52, 177, 242)), StrokeThickness = 3,
            StrokeDashArray = [3, 2], IsHitTestVisible = false
        };
        Panel.SetZIndex(_connectionPreview, int.MaxValue);
        _connectionCanvas.Children.Add(_connectionPreview);
        _connectionCapture = button;
        Mouse.Capture(button, CaptureMode.SubTree);
        _connectionCanvas.Cursor = Cursors.Cross;
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_connectionCanvas is null || _connectionPreview is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_connectionCanvas);
        _connectionPreview.X2 = point.X;
        _connectionPreview.Y2 = point.Y;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_connectionPreview is null || _connectionCanvas is null) return;
        var canvas = _connectionCanvas;
        var dropPoint = e.GetPosition(canvas);
        var hit = canvas.InputHitTest(dropPoint) as DependencyObject;
        var targetButton = hit is null ? null : FindParent<ButtonBase>(hit);
        var targetPort = targetButton?.Tag as NodePortViewModel;
        var source = _pendingOutput;
        var shouldConnect = source is not null && targetPort is { Direction: PortDirection.Input };
        if (shouldConnect && targetButton is not null)
        {
            var end = targetButton.TransformToAncestor(canvas).Transform(new Point(targetButton.ActualWidth / 2, targetButton.ActualHeight / 2));
            _connectionPreview.X2 = end.X;
            _connectionPreview.Y2 = end.Y;
        }
        ClearConnectionPreview();
        if (!shouldConnect || source is null || targetPort is null || _vm.SelectedFlow is null) return;
        _vm.SelectedFlow.SelectNode(targetPort.NodeId, false);
        if (_vm.SelectedFlow.Connect(source.NodeId, source.Id, targetPort.NodeId, targetPort.Id, out var error))
            _vm.Log($"连线已创建：{source.Name} → {targetPort.Name}");
        else
            _vm.Log($"连线未创建：{error}");
        _pendingOutput = null;
        e.Handled = true;
    }

    private void ClearConnectionPreview()
    {
        if (_connectionCanvas is not null)
        {
            if (_connectionPreview is not null) _connectionCanvas.Children.Remove(_connectionPreview);
            _connectionCanvas.Cursor = Cursors.Arrow;
        }
        if (_connectionCapture is not null && Mouse.Captured == _connectionCapture) Mouse.Capture(null);
        _connectionCapture = null;
        _connectionPreview = null;
        _connectionCanvas = null;
    }

    private static Canvas? FindWorkflowCanvas(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Canvas { Name: "NodeCanvas" } canvas) return canvas;
        return null;
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm.SelectedFlow is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _vm.SelectedFlow.AdjustZoom(e.Delta > 0 ? 0.1 : -0.1); e.Handled = true;
    }

    private void CanvasZoomIn_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.AdjustZoom(0.1);
    private void CanvasZoomOut_Click(object sender, RoutedEventArgs e) => _vm.SelectedFlow?.AdjustZoom(-0.1);

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.ChangedButton != MouseButton.Middle) return;
        _canvasScrollViewer = scrollViewer;
        _lastCanvasPointer = e.GetPosition(scrollViewer);
        _panningCanvas = true;
        scrollViewer.Cursor = Cursors.SizeAll;
        scrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panningCanvas || _canvasScrollViewer is not { } scrollViewer || e.MiddleButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(scrollViewer);
        var delta = pointer - _lastCanvasPointer;
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - delta.X);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - delta.Y);
        _lastCanvasPointer = pointer;
        e.Handled = true;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panningCanvas || e.ChangedButton != MouseButton.Middle) return;
        _panningCanvas = false;
        if (_canvasScrollViewer is { } scrollViewer)
        {
            scrollViewer.ReleaseMouseCapture();
            scrollViewer.Cursor = Cursors.Arrow;
        }
        _canvasScrollViewer = null;
        e.Handled = true;
    }

    private void Parameter_LostFocus(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: NodeParameterViewModel parameter }) parameter.Commit(); }

    private async void ScanCameras_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || !_vm.CanConfigureCamera) return;
        try { await _vm.ScanCamerasAsync(); _vm.LoadSelectedCameraDevice(); }
        catch (Exception ex) { ShowError("扫描相机失败", ex); }
    }

    private async void CameraInterface_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _closing || !_vm.CanConfigureCamera || _vm.SelectedCameraInterface is null) return;
        try { await _vm.ScanCamerasAsync(); }
        catch (Exception ex) { _vm.Log($"扫描 HALCON 接口 {_vm.SelectedCameraInterface.Id} 失败：{ex.Message}"); }
    }

    private async void ConnectCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || !_vm.CanConfigureCamera) return;
        try
        {
            using var image = await _vm.ConnectSelectedCameraAsync();
            if (_closing) return;
            image.GetImageSize(out _previewImageWidth, out _previewImageHeight);
            _imagePresenter.Display(image, PreviewWindow.HalconWindow, _previewImageWidth, _previewImageHeight,
                PreviewHost.ActualWidth, PreviewHost.ActualHeight);
            _previewImagePath = null;
            PreviewEmptyText.Visibility = Visibility.Collapsed;
            _vm.Log($"相机首帧已显示：{_vm.SelectedCameraInterface?.Id} / {_vm.SelectedCameraDevice?.Name}，{_previewImageWidth} × {_previewImageHeight}。");
        }
        catch (Exception ex) { _vm.Log($"相机连接失败：{ex.Message}"); ShowError("相机连接失败", ex); }
    }

    private async void DisconnectCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        await StopExecutionAsync();
        await _vm.DisconnectCameraAsync();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _executionTask is not null) return;
        _executionTask = RunOnceAsync();
        try { await _executionTask; }
        finally { _executionTask = null; }
    }

    private async Task RunOnceAsync()
    {
        if (!_vm.HasProject || _vm.SelectedFlow is null) { MessageBox.Show(this, "请先创建或打开项目。", "运行流程", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_vm.IsRunning || _continuousCancellation is not null) return;
        try
        {
            var flow = _vm.SelectedFlow;
            var hasCameraSource = flow.Document.Nodes.Any(node => node.TypeId == HalconVision.Halcon.HalconNodeProvider.CameraCapture);
            if (hasCameraSource && !_vm.IsCameraConnected)
            {
                MessageBox.Show(this, "流程包含相机采集节点，请先在节点属性中连接相机。", "运行流程", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
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
            else { _vm.ReturnImageToSequence(imagePath); DisplaySelectedNodePreview(); }
            if (run.Errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, run.Errors), "流程执行错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError("流程执行失败", ex); }
    }

    private async void ContinuousRun_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _executionTask is not null) return;
        _vm.IsContinuousRunning = true;
        _executionTask = RunContinuouslyAsync();
        try { await _executionTask; }
        finally { _executionTask = null; _vm.IsContinuousRunning = false; }
    }

    private async Task RunContinuouslyAsync()
    {
        if (!_vm.HasProject || _vm.SelectedFlow is null) { MessageBox.Show(this, "请先创建或打开项目。", "连续运行", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_continuousCancellation is not null || _vm.IsRunning) return;
        _continuousCancellation = new CancellationTokenSource();
        try
        {
            var flow = _vm.SelectedFlow;
            var hasCameraSource = flow.Document.Nodes.Any(node => node.TypeId == HalconVision.Halcon.HalconNodeProvider.CameraCapture);
            if (hasCameraSource && !_vm.IsCameraConnected)
            {
                MessageBox.Show(this, "流程包含相机采集节点，请先在节点属性中连接相机。", "连续运行", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var progress = new Progress<NodeExecutionResult>(result => { flow.ApplyNodeResult(result); _vm.Log($"节点 {result.Status}: {result.NodeId}"); });
            var first = true;
            while (!_continuousCancellation.IsCancellationRequested)
            {
                // Always yield to input/rendering, including when every node completes synchronously.
                await Task.Delay(33, _continuousCancellation.Token);
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
                if (first && !hasSequence && !hasCameraSource)
                {
                    MessageBox.Show(this, "连续运行需要配置读取图像文件夹，或添加并连接相机采集节点。", "连续运行", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }
                first = false;
                _vm.Log(hasSequence
                    ? $"连续运行：{Path.GetFileName(imagePath)}"
                    : "连续运行：相机采集下一帧");
                var run = await _vm.RunSelectedFlowAsync(progress, runFlow);
                if (run.Status == WorkflowRunStatus.Cancelled)
                {
                    run.Dispose();
                    _vm.ReturnImageToSequence(imagePath);
                    break;
                }
                _vm.SetExecutionRun(run);
                if (run.Status == WorkflowRunStatus.Success)
                {
                    DisplayLastSuccessfulNode(flow);
                    continue;
                }
                _vm.ReturnImageToSequence(imagePath);
                DisplaySelectedNodePreview();
                if (run.Status != WorkflowRunStatus.Cancelled && run.Errors.Count > 0)
                    MessageBox.Show(this, string.Join(Environment.NewLine, run.Errors), "连续运行错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
            _vm.Log(_continuousCancellation.IsCancellationRequested ? "连续运行已停止。" : "连续运行结束。");
        }
        catch (OperationCanceledException) when (_continuousCancellation.IsCancellationRequested) { _vm.Log("连续运行已停止。"); }
        catch (Exception ex) { ShowError("连续运行失败", ex); }
        finally
        {
            _continuousCancellation.Dispose();
            _continuousCancellation = null;
            // The last run remains available for inspection; acquisition is no longer active.
            await _vm.DisconnectCameraAsync();
        }
    }

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _continuousCancellation?.Cancel();
        _vm.CancelRun();
    }

    private void DisplayLastSuccessfulNode(FlowTabViewModel flow)
    {
        if (_detectionMode && flow.SelectedNode is not null) { DisplaySelectedNodePreview(); return; }
        var nodeResult = _vm.LastRun?.NodeResults.LastOrDefault(result => result.Status == NodeExecutionStatus.Success);
        if (nodeResult is null) return;
        flow.SelectNode(nodeResult.NodeId, false);
        DisplaySelectedNodePreview();
    }

    private void DisplaySelectedNodePreview()
    {
        var node = _vm.SelectedFlow?.SelectedNode;
        var run = _vm.LastRun;
        var selectedResult = run is not null && run.FlowId == _vm.SelectedFlow?.Id
            ? run.NodeResults.FirstOrDefault(result => result.NodeId == node?.Id) : null;
        NodeOutputsGrid.ItemsSource = NodeOutputValue.From(selectedResult, node?.Definition);
        NodeOutputsGrid.SelectedIndex = 0;
        DetectionSummary.Text = NodeResultSummary.Describe(selectedResult);
        DetectionSummary.Foreground = NodeResultSummary.Color(selectedResult);
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
        if (_isDrawingRoi) { e.Handled = true; return; }
        _imagePresenter.Zoom(PreviewWindow.HalconWindow, e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void PreviewHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawingRoi || e.ChangedButton != MouseButton.Left) return;
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

    private void DrawRoi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeParameterViewModel parameter } ||
            _vm.SelectedFlow?.SelectedNode is not { TypeId: HalconNodeProvider.StraightLineFind } lineNode) return;
        var flow = _vm.SelectedFlow;
        var edge = flow.Document.Edges.FirstOrDefault(item => item.ToNodeId == lineNode.Id && item.ToPortId == "image");
        var sourceNode = edge is null ? null : flow.Document.Nodes.FirstOrDefault(item => item.Id == edge.FromNodeId);
        if (sourceNode is null)
        {
            MessageBox.Show(this, "请先将读取图像或相机采集节点连接到直线查找节点。", "绘制检测范围", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var displayed = false;
            if (_vm.LastRun is { } run && run.FlowId == flow.Id && run.RuntimeOutputs?.TryGetValue(sourceNode.Id, out var runtime) == true &&
                runtime.TryGetValue("image", out var imageValue) && imageValue is HImage image)
            {
                if (image.CountObj() > 1)
                {
                    using var first = image.SelectObj(1);
                    first.GetImageSize(out _previewImageWidth, out _previewImageHeight);
                    _imagePresenter.Display(first, PreviewWindow.HalconWindow, _previewImageWidth, _previewImageHeight, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
                }
                else
                {
                    image.GetImageSize(out _previewImageWidth, out _previewImageHeight);
                    _imagePresenter.Display(image, PreviewWindow.HalconWindow, _previewImageWidth, _previewImageHeight, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
                }
                displayed = true;
            }
            else if (sourceNode.TypeId == HalconNodeProvider.ImageRead && sourceNode.Parameters.TryGetValue("path", out var pathValue))
            {
                var path = pathValue.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var imagePath = Directory.Exists(path) ? EnumerateImageFiles(path).FirstOrDefault() : path;
                    if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                    {
                        _previewImagePath = imagePath;
                        var dimensions = _imagePresenter.Display(imagePath, PreviewWindow.HalconWindow);
                        _previewImageWidth = dimensions.Width;
                        _previewImageHeight = dimensions.Height;
                        Dispatcher.BeginInvoke(new Action(() => ImageFit_Click(this, new RoutedEventArgs())));
                        displayed = true;
                    }
                }
            }

            if (!displayed)
            {
                MessageBox.Show(this, "相机图像需先单次运行采集节点；读取图像节点请先设置有效的图像路径。", "绘制检测范围", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PreviewEmptyText.Visibility = Visibility.Collapsed;
            _roiParameter = parameter;
            _isDrawingRoi = true;
            RoiPreviewRectangle.Visibility = Visibility.Collapsed;
            RoiDrawingOverlay.Visibility = Visibility.Visible;
            RoiDrawingOverlay.Cursor = Cursors.Cross;
            RoiDrawingOverlay.Focusable = true;
            RoiDrawingOverlay.Focus();
            DetectionSummary.Text = "正在绘制矩形范围 · 绘制后重新运行检测";
            DetectionSummary.Foreground = Brushes.LightGray;
            _vm.Log("请在图像预览区拖动鼠标，绘制矩形检测范围。");
        }
        catch (Exception ex) { ShowError("准备绘制检测范围失败", ex); }
    }


    private void RoiOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingRoi || e.ChangedButton != MouseButton.Left) return;
        _roiStart = e.GetPosition(RoiDrawingOverlay);
        Canvas.SetLeft(RoiPreviewRectangle, _roiStart.X);
        Canvas.SetTop(RoiPreviewRectangle, _roiStart.Y);
        RoiPreviewRectangle.Width = 0;
        RoiPreviewRectangle.Height = 0;
        RoiPreviewRectangle.Visibility = Visibility.Visible;
        RoiDrawingOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void RoiOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawingRoi || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(RoiDrawingOverlay);
        if (RoiPreviewRectangle.Visibility != Visibility.Visible) return;
        Canvas.SetLeft(RoiPreviewRectangle, Math.Min(_roiStart.X, point.X));
        Canvas.SetTop(RoiPreviewRectangle, Math.Min(_roiStart.Y, point.Y));
        RoiPreviewRectangle.Width = Math.Abs(point.X - _roiStart.X);
        RoiPreviewRectangle.Height = Math.Abs(point.Y - _roiStart.Y);
        e.Handled = true;
    }

    private void RoiOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingRoi || e.ChangedButton != MouseButton.Left || _roiParameter is null) return;
        RoiDrawingOverlay.ReleaseMouseCapture();
        var end = e.GetPosition(RoiDrawingOverlay);
        if (Math.Abs(end.X - _roiStart.X) < 4 || Math.Abs(end.Y - _roiStart.Y) < 4)
        {
            RoiPreviewRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewWindow.HalconWindow.GetPart(out int row1, out int column1, out int row2, out int column2);
        var rowSpan = row2 - row1 + 1d;
        var columnSpan = column2 - column1 + 1d;
        var startRow = row1 + _roiStart.Y / RoiDrawingOverlay.ActualHeight * rowSpan;
        var startColumn = column1 + _roiStart.X / RoiDrawingOverlay.ActualWidth * columnSpan;
        var endRow = row1 + end.Y / RoiDrawingOverlay.ActualHeight * rowSpan;
        var endColumn = column1 + end.X / RoiDrawingOverlay.ActualWidth * columnSpan;
        _roiParameter.ValueText = string.Join(",", new[] { startRow, startColumn, endRow, endColumn }
            .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        _roiParameter.Commit();
        DetectionSummary.Text = "矩形范围已修改 · 请重新运行检测";
        _vm.Log($"已设置检测范围：行 {Math.Min(startRow, endRow):F0}–{Math.Max(startRow, endRow):F0}，列 {Math.Min(startColumn, endColumn):F0}–{Math.Max(startColumn, endColumn):F0}。");
        FinishRoiDrawing();
        e.Handled = true;
    }

    private void FinishRoiDrawing()
    {
        _isDrawingRoi = false;
        _roiParameter = null;
        RoiDrawingOverlay.Visibility = Visibility.Collapsed;
        RoiDrawingOverlay.Cursor = Cursors.Arrow;
        RoiPreviewRectangle.Visibility = Visibility.Collapsed;
    }

    private void Preview_HMouseDown(object sender, HalconDotNet.HSmartWindowControlWPF.HMouseEventArgsWPF e) { }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isDrawingRoi && e.Key == Key.Escape) { FinishRoiDrawing(); e.Handled = true; return; }
        if (_detectionMode)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) { Save_Click(sender, e); e.Handled = true; }
            return;
        }
        var isTextEditing = Keyboard.FocusedElement is TextBoxBase ||
            Keyboard.FocusedElement is DependencyObject focused && FindParent<DataGrid>(focused) is not null;
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

    private async Task PrepareProjectSwitchAsync()
    {
        _switchingProject = true;
        try { await StopExecutionAsync(); await _vm.WaitForCameraOperationsAsync(); }
        finally { _switchingProject = false; }
    }

    private async Task StopExecutionAsync()
    {
        _continuousCancellation?.Cancel();
        _vm.CancelRun();
        if (_executionTask is { } task) await task;
    }

    private void ClearPreview()
    {
        NodeOutputsGrid.ItemsSource = NodeOutputValue.From(null, null);
        DetectionSummary.Text = "未运行 · 选择节点查看结果";
        DetectionSummary.Foreground = Brushes.LightGray;
        PreviewWindow.HalconWindow.ClearWindow();
        _previewImagePath = null;
        _previewImageWidth = _previewImageHeight = 0;
        PreviewEmptyText.Visibility = Visibility.Visible;
        PreviewEmptyText.Text = "选择并运行图像节点以显示结果";
        foreach (var viewer in OwnedWindows.OfType<ImageViewerWindow>().ToArray()) viewer.Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        if (!ConfirmSaveBeforeSwitch()) { e.Cancel = true; return; }
        _closing = true;
        await StopExecutionAsync();
        await _vm.ShutdownAsync();
        ClearPreview();
        PreviewWindow.Dispose();
        _closeReady = true;
        // Cleanup can complete synchronously; close only after the original Closing event returns.
        _ = Dispatcher.BeginInvoke(new Action(Close));
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

    private void ShowError(string title, Exception ex)
    {
        _vm.Log($"{title}：{ex}");
        if (!_closing) MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current)) if (current is T parent) return parent;
        return null;
    }
}
