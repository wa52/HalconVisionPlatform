using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed class DetectionDashboard : DockPanel, IDisposable
{
    private readonly Grid _grid = new();
    private readonly List<DetectionTile> _tiles = [];
    private readonly TextBlock _empty = new() { Text = "点击“添加面板”，组合各节点的图像、参数和结果。", Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Func<WorkflowExecutionRun?> _run = () => null;
    private FlowTabViewModel? _flow;
    private bool _disposed;
    public IReadOnlyList<DetectionTile> Tiles => _tiles;

    public DetectionDashboard()
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        SetDock(toolbar, Dock.Top); Children.Add(toolbar);
        var add = new Button { Content = "添加面板", Padding = new Thickness(12, 5, 12, 5) };
        add.Click += (_, _) => AddPanel(); toolbar.Children.Add(add);
        toolbar.Children.Add(new TextBlock { Text = "每个面板独立选择节点、图像 / 输出参数 / 检测结果，以及输出字段；拖动分隔条调整大小", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        Children.Add(_grid);
    }

    public void SetSource(FlowTabViewModel? flow, Func<WorkflowExecutionRun?> run)
    {
        if (_disposed) return;
        _run = run;
        if (!ReferenceEquals(_flow, flow))
        {
            foreach (var tile in _tiles) tile.Dispose(); _tiles.Clear(); _flow = flow;
            foreach (var settings in flow?.Document.DetectionPanels ?? []) AddTile(settings);
            Layout();
        }
        foreach (var tile in _tiles) tile.Refresh();
    }

    public void EnsureInitialPanels()
    {
        if (_disposed || _flow is null || _tiles.Count != 0 || _flow.Nodes.Count == 0) return;
        var source = _flow.Nodes.FirstOrDefault(node => node.TypeId is "halcon.image-read" or "halcon.camera-capture") ?? _flow.Nodes.First();
        var result = _flow.SelectedNode ?? _flow.Nodes.Last();
        AddTile(new(Guid.NewGuid(), source.Id));
        AddTile(new(Guid.NewGuid(), result.Id, "图像"));
        AddTile(new(Guid.NewGuid(), result.Id, "输出参数"));
        AddTile(new(Guid.NewGuid(), result.Id, "检测结果"));
        Layout(); Save();
    }

    public void AddPanel()
    {
        if (_disposed || _flow is null) return;
        var node = _flow.SelectedNode ?? _flow.Nodes.FirstOrDefault();
        if (node is null) return;
        AddTile(new(Guid.NewGuid(), node.Id)); Layout(); Save();
    }

    private void AddTile(DetectionPanelSettings settings)
    {
        var tile = new DetectionTile(settings, () => _flow, () => _run());
        tile.Changed += _ => Save();
        tile.RemoveRequested += RemovePanel;
        _tiles.Add(tile);
    }

    public void RemovePanel(DetectionTile tile)
    {
        if (!_tiles.Remove(tile)) return;
        tile.Dispose(); Layout(); Save();
    }

    private void Save() => _flow?.SetDetectionPanels(_tiles.Select(tile => tile.Settings));

    private void Layout()
    {
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        if (_tiles.Count == 0) { _grid.Children.Add(_empty); Grid.SetColumnSpan(_empty, 3); return; }
        var count = (_tiles.Count + 1) / 2;
        for (var row = 0; row < count; row++)
        {
            if (row > 0)
            {
                _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                var horizontal = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.SlateGray, ResizeDirection = GridResizeDirection.Rows };
                Grid.SetRow(horizontal, row * 2 - 1); Grid.SetColumnSpan(horizontal, 3); _grid.Children.Add(horizontal);
            }
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 150 });
        }
        for (var index = 0; index < _tiles.Count; index++)
        {
            var tile = _tiles[index]; Grid.SetRow(tile, index / 2 * 2); Grid.SetColumn(tile, index % 2 * 2);
            Grid.SetColumnSpan(tile, index == _tiles.Count - 1 && _tiles.Count % 2 == 1 ? 3 : 1);
            _grid.Children.Add(tile);
        }
        var vertical = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.SlateGray, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(vertical, 1); Grid.SetRowSpan(vertical, count * 2 - 1); _grid.Children.Add(vertical);
    }

    public void Dispose() { if (_disposed) return; _disposed = true; foreach (var tile in _tiles) tile.Dispose(); _tiles.Clear(); _grid.Children.Clear(); }
}
