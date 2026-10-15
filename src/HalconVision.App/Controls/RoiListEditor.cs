using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed class RoiDrawRequestedEventArgs(RoutedEvent routedEvent, NodeParameterViewModel parameter, Guid? id) : RoutedEventArgs(routedEvent)
{
    public NodeParameterViewModel Parameter { get; } = parameter;
    public Guid? RoiId { get; } = id;
}

public sealed class RoiListEditor : UserControl
{
    public static readonly DependencyProperty ParameterProperty = DependencyProperty.Register(nameof(Parameter), typeof(NodeParameterViewModel), typeof(RoiListEditor),
        new PropertyMetadata(null, (owner, e) => ((RoiListEditor)owner).Attach(e.OldValue as NodeParameterViewModel)));
    public static readonly RoutedEvent DrawRequestedEvent = EventManager.RegisterRoutedEvent(nameof(DrawRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RoiListEditor));
    public NodeParameterViewModel? Parameter { get => (NodeParameterViewModel?)GetValue(ParameterProperty); set => SetValue(ParameterProperty, value); }
    public event RoutedEventHandler DrawRequested { add => AddHandler(DrawRequestedEvent, value); remove => RemoveHandler(DrawRequestedEvent, value); }
    private readonly ObservableCollection<RoiRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly TextBlock _hint = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private bool _writing, _attached;
    public RoiListEditor()
    {
        var root = new StackPanel(); Content = root;
        var buttons = new WrapPanel(); root.Children.Add(buttons);
        _grid = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            MaxHeight = 210, MinHeight = 56, Background = new SolidColorBrush(Color.FromRgb(27, 39, 49)),
            Foreground = Brushes.LightGray, RowBackground = new SolidColorBrush(Color.FromRgb(27, 39, 49)), HeadersVisibility = DataGridHeadersVisibility.Column };
        Add("新增 ROI", () => Request(null));
        Add("重绘选中", () => { if (_grid.SelectedItem is RoiRow row) Request(row.Id); });
        Add("删除选中", () => { if (_grid.SelectedItem is RoiRow row) { _rows.Remove(row); Persist(); } });
        Add("清空", () => { _rows.Clear(); Persist(); });
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = new Binding(nameof(RoiRow.Enabled)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 40 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "名称（可编辑）", Binding = new Binding(nameof(RoiRow.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "行列范围", Binding = new Binding(nameof(RoiRow.Coordinates)), IsReadOnly = true, Width = 120 });
        root.Children.Add(_grid); root.Children.Add(_hint);
        Loaded += (_, _) => Attach(null);
        Unloaded += (_, _) => { if (_attached && Parameter is { } parameter) parameter.PropertyChanged -= ParameterChanged; _attached = false; };
        void Add(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 3, 3) };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
    }
    private void Request(Guid? id)
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (Parameter is null) return;
        if (Parameter.Error is not null) { _hint.Text = "请先修正 ROI 名称或参数错误。"; return; }
        if (id is null && _rows.Count >= DetectionRoi.MaximumCount) { _hint.Text = $"最多 {DetectionRoi.MaximumCount} 个 ROI。"; return; }
        RaiseEvent(new RoiDrawRequestedEventArgs(DrawRequestedEvent, Parameter, id));
    }
    private void Attach(NodeParameterViewModel? previous)
    {
        if (previous is not null) previous.PropertyChanged -= ParameterChanged;
        if (_attached && Parameter is { } current) current.PropertyChanged -= ParameterChanged;
        _attached = false;
        if (Parameter is { IsRoiList: true } parameter) { parameter.PropertyChanged += ParameterChanged; _attached = true; Reload(); }
    }
    private void ParameterChanged(object? sender, PropertyChangedEventArgs e)
    { if (!_writing && e.PropertyName == nameof(NodeParameterViewModel.ValueText)) Reload(); }
    private void Reload()
    {
        try
        {
            var selected = (_grid.SelectedItem as RoiRow)?.Id;
            _rows.Clear();
            foreach (var roi in Parameter!.ReadRois()) _rows.Add(new(roi, Persist));
            _grid.SelectedItem = _rows.FirstOrDefault(item => item.Id == selected) ?? _rows.LastOrDefault();
            Hint();
        }
        catch (Exception ex) { _hint.Text = ex.Message; }
    }
    private void Persist()
    {
        if (Parameter is null) return;
        _writing = true;
        try { Parameter.SetRois(_rows.Select(item => item.Value)); Hint(); }
        catch (Exception ex) { _hint.Text = ex.Message; }
        finally { _writing = false; }
    }
    private void Hint() => _hint.Text = _rows.Count == 0 ? "未设置 ROI，使用全图推理。" : $"{_rows.Count} 个 ROI；逐个裁剪推理，名称和范围随项目保存。";

    public sealed class RoiRow(DetectionRoi roi, Action changed) : INotifyPropertyChanged
    {
        public DetectionRoi Value { get; private set; } = roi;
        public Guid Id => Value.Id;
        public string Name { get => Value.Name; set { Value = Value with { Name = value?.Trim() ?? "" }; PropertyChanged?.Invoke(this, new(nameof(Name))); changed(); } }
        public bool Enabled { get => Value.Enabled; set { Value = Value with { Enabled = value }; PropertyChanged?.Invoke(this, new(nameof(Enabled))); changed(); } }
        public string Coordinates => $"({Value.RowBegin:F0},{Value.ColumnBegin:F0})–({Value.RowEnd:F0},{Value.ColumnEnd:F0})";
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
