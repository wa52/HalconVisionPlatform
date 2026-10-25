using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HalconDotNet;
using HalconVision.Domain;
using HalconVision.Halcon;

namespace HalconVision.App;

public sealed class DetectionContentView : Border, IDisposable
{
    private readonly Func<WorkflowExecutionRun?> _run;
    private readonly Func<FlowTabViewModel?> _flow;
    private readonly ComboBox _node = new() { Width = 140, DisplayMemberPath = "DisplayName" };
    private readonly ComboBox _view = new() { Width = 95, ItemsSource = new[] { "图像", "输出参数", "检测结果" } };
    private readonly ComboBox _field = new() { MinWidth = 110, MaxWidth = 220 };
    private readonly ComboBox _imageIndex = new() { MinWidth = 95, MaxWidth = 180, Visibility = Visibility.Collapsed };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _empty = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _imageHost = new() { Background = Brushes.Black };
    private readonly HSmartWindowControlWPF _image = new();
    private readonly Canvas _overlayCanvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly StackPanel _layerChoices = new();
    private readonly HashSet<Guid> _expandedAdvanced = [];
    private readonly Expander _layerEditor = new() { Header = "叠加节点与显示字段（勾选）", Foreground = Brushes.LightGray, Margin = new Thickness(6) };
    private DetectionOverlayScene _scene = new();
    private readonly DataGrid _values = new() { AutoGenerateColumns = false, IsReadOnly = true, Background = new SolidColorBrush(Color.FromRgb(27, 39, 49)), Foreground = Brushes.LightGray, RowBackground = new SolidColorBrush(Color.FromRgb(27, 39, 49)) };
    private readonly HalconImagePresenter _presenter = new();
    private bool _updating, _disposed, _panning;
    private readonly bool _configurationMode;
    private Point _pointer;
    private int _width, _height;
    private Guid _lastSource;
    private string _lastPort = "";
    private int _lastImageIndex;
    public DetectionContentSettings Settings { get; private set; }
    public event Action<DetectionContentView>? Changed;
    public event Action<DetectionContentView>? RemoveRequested;

    public DetectionContentView(DetectionContentSettings settings, Func<FlowTabViewModel?> flow, Func<WorkflowExecutionRun?> run, bool configurationMode = false)
    {
        _configurationMode = configurationMode;
        Settings = settings; _flow = flow; _run = run;
        Margin = new Thickness(3); BorderBrush = new SolidColorBrush(Color.FromRgb(52, 67, 78)); BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromRgb(27, 39, 49));
        var root = new DockPanel { ClipToBounds = true }; Child = root;
        var tools = new WrapPanel { Margin = new Thickness(5), Background = new SolidColorBrush(Color.FromRgb(32, 46, 57)) };
        tools.Visibility = configurationMode ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        tools.Children.Add(_node); tools.Children.Add(_view); tools.Children.Add(_field); tools.Children.Add(_imageIndex);
        AddButton("−", () => Zoom(0.8)); AddButton("+", () => Zoom(1.25)); AddButton("适应", Fit);
        AddButton("复制数据", () => { if (_values.SelectedItem is NodeOutputValue value) Clipboard.SetText(value.FullValue); });
        AddButton("移除内容", () => RemoveRequested?.Invoke(this));
        DockPanel.SetDock(_summary, Dock.Top); root.Children.Add(_summary);
        var content = new Grid { MinHeight = _configurationMode ? 120 : 60 }; root.Children.Add(content);
        content.Children.Add(_imageHost); _imageHost.Children.Add(_image); _imageHost.Children.Add(_overlayCanvas); _imageHost.Children.Add(_empty); content.Children.Add(_values);
        _layerEditor.Content = new ScrollViewer { Content = _layerChoices, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(_layerEditor, Dock.Top); root.Children.Insert(1, _layerEditor);
        _values.Columns.Add(new DataGridTextColumn { Header = "字段", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(180) });
        _values.Columns.Add(new DataGridTextColumn { Header = "当前值", Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 });
        _node.SelectionChanged += (_, _) => Configure(true); _view.SelectionChanged += (_, _) => Configure(true); _field.SelectionChanged += (_, _) => Configure();
        _imageIndex.SelectionChanged += (_, _) => Configure();
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        _image.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Loaded);
        SizeChanged += (_, _) => { if (IsLoaded && _width > 0 && _imageHost.IsVisible) Fit(); };
        _imageHost.SizeChanged += (_, _) => { if (IsLoaded && _width > 0 && _imageHost.IsVisible) Fit(); };
        _imageHost.PreviewMouseWheel += (_, e) => { Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true; };
        _imageHost.ToolTip = "滚轮缩放，拖动平移，双击适应图像";
        _imageHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _panning = false; _imageHost.ReleaseMouseCapture(); Fit(); e.Handled = true; return; }
            _panning = true; _pointer = e.GetPosition(_imageHost); _imageHost.CaptureMouse();
        };
        _imageHost.PreviewMouseMove += (_, e) => { if (!_panning || e.LeftButton != MouseButtonState.Pressed) return; var point = e.GetPosition(_imageHost); var delta = point - _pointer; _presenter.Pan(_image.HalconWindow, delta.X, delta.Y, _imageHost.ActualWidth, _imageHost.ActualHeight); _pointer = point; RenderOverlays(); };
        _imageHost.PreviewMouseLeftButtonUp += (_, _) => { _panning = false; _imageHost.ReleaseMouseCapture(); };
        Refresh();

        void AddButton(string name, Action action) { var button = new Button { Content = name, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(3, 0, 0, 0) }; button.Click += (_, _) => action(); tools.Children.Add(button); }
    }

    private void Configure(bool resetField = false)
    {
        if (_updating || _disposed) return;
        Settings = Settings with { NodeId = (_node.SelectedItem as FlowNodeViewModel)?.Id ?? Settings.NodeId,
            View = _view.SelectedItem as string ?? Settings.View, OutputField = resetField ? "全部" : _field.SelectedItem as string ?? "全部",
            ImageIndex = resetField ? 1 : _imageIndex.SelectedIndex >= 0 ? _imageIndex.SelectedIndex + 1 : Settings.ImageIndex };
        Changed?.Invoke(this); Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var flow = _flow(); var node = flow?.Nodes.FirstOrDefault(item => item.Id == Settings.NodeId);
        var run = _run(); var result = run?.FlowId == flow?.Id ? run?.NodeResults.FirstOrDefault(item => item.NodeId == Settings.NodeId) : null;
        var rows = NodeOutputValue.From(result, node?.Definition);
        _updating = true;
        try
        {
            _node.ItemsSource = flow?.Nodes; _node.SelectedItem = node; _view.SelectedItem = Settings.View;
            var fields = Settings.View == "图像" ? node?.OutputPorts.Where(port => port.DataType is "halcon.image" or "halcon.region" or "halcon.xld").Select(port => port.Id) ?? [] : rows.Select(row => row.Name);
            _field.ItemsSource = new[] { "全部" }.Concat(fields).Distinct().ToArray();
            _field.SelectedItem = _field.Items.Cast<string>().Contains(Settings.OutputField) ? Settings.OutputField : "全部";
        }
        finally { _updating = false; }
        _summary.Text = node is null ? "源节点已删除，请重新选择" : $"{node.DisplayName} · {NodeResultSummary.Describe(result)}";
        _summary.Foreground = NodeResultSummary.Color(result);
        var imageView = Settings.View == "图像";
        _summary.Visibility = !_configurationMode && imageView && Settings.Overlays?.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        _imageIndex.Visibility = Visibility.Collapsed;
        _imageHost.Visibility = imageView ? Visibility.Visible : Visibility.Collapsed;
        _layerEditor.Visibility = _configurationMode && imageView ? Visibility.Visible : Visibility.Collapsed;
        if (_configurationMode) BuildLayerChoices(flow, run);
        _values.Visibility = imageView ? Visibility.Collapsed : Visibility.Visible;
        _values.ItemsSource = Settings.OutputField != "全部" ? rows.Where(row => row.Name == Settings.OutputField || row.Name.StartsWith(Settings.OutputField + "."))
            : Settings.View == "检测结果" ? rows.Where(row => row.Name is "检测结果" or "score" or "count" or "angle" or "length" or "area" or "diameter" or "centerDistance" or "boundaryDistance" or "centerToLine" or "boundaryToLine" or "signedClearance" or "objects" or "classes" or "classId" or "className" or "confidence" or "scores" || row.Name.StartsWith("status.") || row.Name.StartsWith("decision.") || row.Name.StartsWith("roiResults")) : rows;
        if (!imageView || !IsLoaded || !IsVisible || _image.HalconWindow is null) return;
        var oldWidth = _width; var oldHeight = _height;
        var preservePart = oldWidth > 0 && _lastSource == Settings.NodeId && _lastPort == Settings.OutputField && _lastImageIndex == Settings.ImageIndex;
        _image.HalconWindow.GetPart(out int oldRow1, out int oldCol1, out int oldRow2, out int oldCol2);
        _image.HalconWindow.ClearWindow(); _overlayCanvas.Children.Clear(); _scene = new(); _width = _height = 0;
        if (run is null || run.FlowId != flow?.Id || run.RuntimeOutputs is null || !run.RuntimeOutputs.TryGetValue(Settings.NodeId, out var runtime))
        { _empty.Text = "此节点尚无本次运行图像"; _empty.Visibility = Visibility.Visible; return; }
        var iconic = Settings.OutputField == "全部" ? runtime.Values.OfType<HObject>().FirstOrDefault() : runtime.GetValueOrDefault(Settings.OutputField) as HObject;
        if (iconic is null || !iconic.IsInitialized()) { _empty.Text = "所选输出没有图像"; _empty.Visibility = Visibility.Visible; return; }
        try
        {
            if (iconic is HImage image)
            {
                var count = image.CountObj();
                _updating = true;
                try
                {
                    var names = result?.Outputs.TryGetValue("roiResults", out var roiResults) == true && roiResults.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? roiResults.EnumerateArray().Select(item => item.GetProperty("RoiName").GetString() ?? "ROI").ToArray() : [];
                    _imageIndex.ItemsSource = Enumerable.Range(1, count).Select(index => names.Length == count ? names[index - 1] : $"图像 {index}").ToArray();
                    _imageIndex.SelectedIndex = Math.Clamp(Settings.ImageIndex - 1, 0, Math.Max(0, count - 1));
                    _imageIndex.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
                }
                finally { _updating = false; }
                using var first = image.SelectObj(Math.Clamp(Settings.ImageIndex, 1, Math.Max(1, count))); first.GetImageSize(out _width, out _height);
                _presenter.Display(first, _image.HalconWindow, _width, _height, _imageHost.ActualWidth, _imageHost.ActualHeight);
            }
            else
            {
                foreach (var value in result!.Outputs.Values)
                    if (value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty("width", out var width) && value.TryGetProperty("height", out var height)) { _width = width.GetInt32(); _height = height.GetInt32(); break; }
                _presenter.Display(iconic, _image.HalconWindow, _width, _height, _imageHost.ActualWidth, _imageHost.ActualHeight);
            }
            _empty.Visibility = Visibility.Collapsed;
            if (preservePart && oldWidth == _width && oldHeight == _height)
                _image.HalconWindow.SetPart(oldRow1, oldCol1, oldRow2, oldCol2);
            _lastSource = Settings.NodeId; _lastPort = Settings.OutputField; _lastImageIndex = Settings.ImageIndex;
            if (flow is not null) _scene = DetectionOverlayScene.Create(flow, run, Settings, _width, _height);
            RenderOverlays();
        }
        catch (Exception ex) { _empty.Text = $"图像显示失败：{ex.Message}"; _empty.Visibility = Visibility.Visible; }
    }

    public void SetOverlay(DetectionOverlaySettings layer, bool enabled = true)
    {
        if (_disposed) return;
        var layers = (Settings.Overlays ?? []).Where(item => item.NodeId != layer.NodeId).ToList();
        if (enabled) layers.Add(layer);
        Settings = Settings with { Overlays = layers }; Changed?.Invoke(this); Refresh();
    }

    private void BuildLayerChoices(FlowTabViewModel? flow, WorkflowExecutionRun? run)
    {
        _layerChoices.Children.Clear();
        foreach (var candidate in flow?.Nodes ?? [])
        {
            var layer = Settings.Overlays?.FirstOrDefault(item => item.NodeId == candidate.Id);
            var enabled = layer is not null;
            layer ??= new(candidate.Id);
            var row = new WrapPanel { Margin = new Thickness(3) };
            var select = new CheckBox { Content = candidate.DisplayName, IsChecked = enabled, Width = 150, Foreground = Brushes.White };
            IconUi.SetKeepText(select, true);
            select.Checked += (_, _) => SetOverlay(layer); select.Unchecked += (_, _) => SetOverlay(layer, false); row.Children.Add(select);
            var geometry = new CheckBox { Content = "图形", IsChecked = layer.ShowGeometry, IsEnabled = enabled, Foreground = Brushes.Gold, Margin = new Thickness(8, 0, 8, 0) };
            geometry.Checked += (_, _) => SetOverlay(layer with { ShowGeometry = true }); geometry.Unchecked += (_, _) => SetOverlay(layer with { ShowGeometry = false }); row.Children.Add(geometry);
            var decision = new CheckBox { Content = "判定", IsChecked = layer.ShowDecision, IsEnabled = enabled, Foreground = Brushes.LightGreen };
            decision.Checked += (_, _) => SetOverlay(layer with { ShowDecision = true }); decision.Unchecked += (_, _) => SetOverlay(layer with { ShowDecision = false }); row.Children.Add(decision);
            _layerChoices.Children.Add(row);
            var fields = new WrapPanel { Margin = new Thickness(20, 0, 0, 3), IsEnabled = enabled };
            var result = run?.FlowId == flow?.Id ? run?.NodeResults.FirstOrDefault(item => item.NodeId == candidate.Id) : null;
            var available = result is null ? candidate.OutputPorts.Select(port => port.Id).ToHashSet()
                : result.Outputs.Keys.ToHashSet();
            var names = DetectionOverlayScene.ImportantFields.Keys.Where(available.Contains);
            foreach (var name in names)
            {
                AddField(fields, name, DetectionOverlayScene.ImportantFields[name]);
            }
            _layerChoices.Children.Add(fields);
            var advanced = new WrapPanel { IsEnabled = enabled };
            var outputNames = NodeOutputValue.From(result, candidate.Definition).Select(item => item.Name).ToHashSet();
            foreach (var (name, label) in DetectionOverlayScene.AdvancedFields)
                if (outputNames.Contains(name) || (result is null && candidate.OutputPorts.Any(port => port.Id == name || name.StartsWith("line.") && port.Id == "line")))
                    AddField(advanced, name, label);
            if (advanced.Children.Count > 0)
            {
                var section = new Expander { Header = "高级数据", Foreground = Brushes.LightGray,
                    Margin = new Thickness(20, 0, 0, 4), Content = advanced, IsExpanded = _expandedAdvanced.Contains(candidate.Id) };
                section.Expanded += (_, _) => _expandedAdvanced.Add(candidate.Id);
                section.Collapsed += (_, _) => _expandedAdvanced.Remove(candidate.Id);
                _layerChoices.Children.Add(section);
            }

            void AddField(WrapPanel host, string name, string label)
            {
                var chosen = (layer.Fields ?? DetectionOverlayScene.ImportantFields.Keys.ToList())
                    .Where(field => DetectionOverlayScene.ImportantFields.ContainsKey(field) || DetectionOverlayScene.AdvancedFields.ContainsKey(field)).ToList();
                var check = new CheckBox { Content = label, IsChecked = chosen.Contains(name), Foreground = Brushes.LightGray, Margin = new Thickness(6, 0, 6, 0) };
                IconUi.SetKeepText(check, true);
                check.Checked += (_, _) => SetOverlay(layer with { Fields = chosen.Append(name).Distinct().ToList() });
                check.Unchecked += (_, _) => SetOverlay(layer with { Fields = chosen.Where(field => field != name).ToList() });
                host.Children.Add(check);
            }
        }
    }

    private void RenderOverlays()
    {
        _overlayCanvas.Children.Clear();
        if (_disposed || _width <= 0 || _imageHost.ActualWidth <= 0 || _imageHost.ActualHeight <= 0) return;
        _image.HalconWindow.GetPart(out int row1, out int col1, out int row2, out int col2);
        Point Position(Point2D point) => new((point.Column - col1 + 0.5) * _imageHost.ActualWidth / (col2 - col1 + 1d),
            (point.Row - row1 + 0.5) * _imageHost.ActualHeight / (row2 - row1 + 1d));
        foreach (var shape in _scene.Geometry)
            _overlayCanvas.Children.Add(new System.Windows.Shapes.Polyline { Points = new PointCollection(shape.Points.Select(Position)),
                Stroke = (Brush)new BrushConverter().ConvertFromString(shape.Color)!, StrokeThickness = shape.Color == "Red" ? 2.5 : 1.5, IsHitTestVisible = false });
        var top = 8d;
        var occupied = new List<Rect>();
        foreach (var caption in _scene.Captions)
        {
            var text = new TextBlock { Text = caption.Text, Foreground = (Brush)new BrushConverter().ConvertFromString(caption.Color)!,
                Background = new SolidColorBrush(Color.FromArgb(185, 10, 20, 28)), FontSize = 12, Padding = new Thickness(4, 2, 4, 2), TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(20, _imageHost.ActualWidth - 16), IsHitTestVisible = false };
            text.Measure(new Size(text.MaxWidth, double.PositiveInfinity));
            if (caption.Position is { } anchor)
            {
                var position = Position(anchor);
                var left = Math.Clamp(position.X + 3, 0, Math.Max(0, _imageHost.ActualWidth - text.DesiredSize.Width));
                var maxTop = Math.Max(0, _imageHost.ActualHeight - text.DesiredSize.Height);
                var desiredTop = Math.Clamp(position.Y - text.DesiredSize.Height - 2, 0, maxTop);
                var chosenTop = desiredTop;
                var stride = Math.Max(1, text.DesiredSize.Height + 3);
                for (var step = 0; step <= Math.Ceiling(_imageHost.ActualHeight / stride); step++)
                {
                    var candidates = step == 0 ? new[] { desiredTop } : new[] { desiredTop - step * stride, desiredTop + step * stride };
                    var free = candidates.Where(y => y >= 0 && y <= maxTop).Select(y => (double?)y).FirstOrDefault(y =>
                        occupied.All(rectangle => !rectangle.IntersectsWith(new Rect(left, y!.Value, text.DesiredSize.Width, text.DesiredSize.Height))));
                    if (free is null) continue;
                    chosenTop = free.Value; break;
                }
                Canvas.SetLeft(text, left); Canvas.SetTop(text, chosenTop);
            }
            else { Canvas.SetLeft(text, 8); Canvas.SetTop(text, top); top += text.DesiredSize.Height + 3; }
            occupied.Add(new Rect(Canvas.GetLeft(text), Canvas.GetTop(text), text.DesiredSize.Width, text.DesiredSize.Height));
            _overlayCanvas.Children.Add(text);
        }
    }

    private void Zoom(double factor) { if (!_disposed && _width > 0) { _presenter.Zoom(_image.HalconWindow, factor); RenderOverlays(); } }
    private void Fit() { if (!_disposed && _width > 0) { _presenter.Fit(_image.HalconWindow, _width, _height, _imageHost.ActualWidth, _imageHost.ActualHeight); RenderOverlays(); } }
    public void Dispose() { if (_disposed) return; _disposed = true; _imageHost.ReleaseMouseCapture(); _image.Dispose(); }
}
