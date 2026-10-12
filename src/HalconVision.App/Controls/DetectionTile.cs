using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HalconDotNet;
using HalconVision.Domain;
using HalconVision.Halcon;

namespace HalconVision.App;

public sealed class DetectionTile : Border, IDisposable
{
    private readonly Func<WorkflowExecutionRun?> _run;
    private readonly Func<FlowTabViewModel?> _flow;
    private readonly ComboBox _node = new() { Width = 140, DisplayMemberPath = "DisplayName" };
    private readonly ComboBox _view = new() { Width = 95, ItemsSource = new[] { "图像", "输出参数", "检测结果" } };
    private readonly ComboBox _field = new() { MinWidth = 110, MaxWidth = 220 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _empty = new() { Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _imageHost = new() { Background = Brushes.Black };
    private readonly HSmartWindowControlWPF _image = new();
    private readonly DataGrid _values = new() { AutoGenerateColumns = false, IsReadOnly = true, Background = new SolidColorBrush(Color.FromRgb(27, 39, 49)), Foreground = Brushes.LightGray, RowBackground = new SolidColorBrush(Color.FromRgb(27, 39, 49)) };
    private readonly HalconImagePresenter _presenter = new();
    private bool _updating, _disposed, _panning;
    private Point _pointer;
    private int _width, _height;
    public DetectionPanelSettings Settings { get; private set; }
    public event Action<DetectionTile>? Changed;
    public event Action<DetectionTile>? RemoveRequested;

    public DetectionTile(DetectionPanelSettings settings, Func<FlowTabViewModel?> flow, Func<WorkflowExecutionRun?> run)
    {
        Settings = settings; _flow = flow; _run = run;
        Margin = new Thickness(3); BorderBrush = new SolidColorBrush(Color.FromRgb(52, 67, 78)); BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromRgb(27, 39, 49));
        var root = new DockPanel(); Child = root;
        var tools = new WrapPanel { Margin = new Thickness(5), Background = new SolidColorBrush(Color.FromRgb(32, 46, 57)) };
        DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        tools.Children.Add(_node); tools.Children.Add(_view); tools.Children.Add(_field);
        AddButton("−", () => Zoom(0.8)); AddButton("+", () => Zoom(1.25)); AddButton("适应", Fit);
        AddButton("复制数据", () => { if (_values.SelectedItem is NodeOutputValue value) Clipboard.SetText(value.FullValue); });
        AddButton("删除面板", () => RemoveRequested?.Invoke(this));
        DockPanel.SetDock(_summary, Dock.Top); root.Children.Add(_summary);
        var content = new Grid(); root.Children.Add(content);
        content.Children.Add(_imageHost); _imageHost.Children.Add(_image); _imageHost.Children.Add(_empty); content.Children.Add(_values);
        _values.Columns.Add(new DataGridTextColumn { Header = "字段", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(180) });
        _values.Columns.Add(new DataGridTextColumn { Header = "当前值", Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 });
        _node.SelectionChanged += (_, _) => Configure(true); _view.SelectionChanged += (_, _) => Configure(true); _field.SelectionChanged += (_, _) => Configure();
        Loaded += (_, _) => Refresh();
        SizeChanged += (_, _) => { if (IsLoaded && _width > 0 && _imageHost.IsVisible) Fit(); };
        _imageHost.PreviewMouseWheel += (_, e) => { Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true; };
        _imageHost.PreviewMouseLeftButtonDown += (_, e) => { _panning = true; _pointer = e.GetPosition(_imageHost); _imageHost.CaptureMouse(); };
        _imageHost.PreviewMouseMove += (_, e) => { if (!_panning || e.LeftButton != MouseButtonState.Pressed) return; var point = e.GetPosition(_imageHost); var delta = point - _pointer; _presenter.Pan(_image.HalconWindow, delta.X, delta.Y, _imageHost.ActualWidth, _imageHost.ActualHeight); _pointer = point; };
        _imageHost.PreviewMouseLeftButtonUp += (_, _) => { _panning = false; _imageHost.ReleaseMouseCapture(); };
        Refresh();

        void AddButton(string name, Action action) { var button = new Button { Content = name, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(3, 0, 0, 0) }; button.Click += (_, _) => action(); tools.Children.Add(button); }
    }

    private void Configure(bool resetField = false)
    {
        if (_updating || _disposed) return;
        Settings = Settings with { NodeId = (_node.SelectedItem as FlowNodeViewModel)?.Id ?? Settings.NodeId,
            View = _view.SelectedItem as string ?? Settings.View, OutputField = resetField ? "全部" : _field.SelectedItem as string ?? "全部" };
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
        _imageHost.Visibility = imageView ? Visibility.Visible : Visibility.Collapsed;
        _values.Visibility = imageView ? Visibility.Collapsed : Visibility.Visible;
        _values.ItemsSource = Settings.OutputField != "全部" ? rows.Where(row => row.Name == Settings.OutputField || row.Name.StartsWith(Settings.OutputField + "."))
            : Settings.View == "检测结果" ? rows.Where(row => row.Name is "检测结果" or "score" or "count" or "angle" or "length" || row.Name.StartsWith("status.")) : rows;
        if (!imageView || !IsLoaded || !IsVisible) return;
        _image.HalconWindow.ClearWindow(); _width = _height = 0;
        if (run is null || run.FlowId != flow?.Id || run.RuntimeOutputs is null || !run.RuntimeOutputs.TryGetValue(Settings.NodeId, out var runtime))
        { _empty.Text = "此节点尚无本次运行图像"; _empty.Visibility = Visibility.Visible; return; }
        var iconic = Settings.OutputField == "全部" ? runtime.Values.OfType<HObject>().FirstOrDefault() : runtime.GetValueOrDefault(Settings.OutputField) as HObject;
        if (iconic is null || !iconic.IsInitialized()) { _empty.Text = "所选输出没有图像"; _empty.Visibility = Visibility.Visible; return; }
        try
        {
            if (iconic is HImage image)
            {
                using var first = image.SelectObj(1); first.GetImageSize(out _width, out _height);
                _presenter.Display(first, _image.HalconWindow, _width, _height, _imageHost.ActualWidth, _imageHost.ActualHeight);
            }
            else
            {
                foreach (var value in result!.Outputs.Values)
                    if (value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty("width", out var width) && value.TryGetProperty("height", out var height)) { _width = width.GetInt32(); _height = height.GetInt32(); break; }
                _presenter.Display(iconic, _image.HalconWindow, _width, _height, _imageHost.ActualWidth, _imageHost.ActualHeight);
            }
            _empty.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { _empty.Text = $"图像显示失败：{ex.Message}"; _empty.Visibility = Visibility.Visible; }
    }

    private void Zoom(double factor) { if (!_disposed && _width > 0) _presenter.Zoom(_image.HalconWindow, factor); }
    private void Fit() { if (!_disposed && _width > 0) _presenter.Fit(_image.HalconWindow, _width, _height, _imageHost.ActualWidth, _imageHost.ActualHeight); }
    public void Dispose() { if (_disposed) return; _disposed = true; _imageHost.ReleaseMouseCapture(); _image.Dispose(); }
}
