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
    private readonly bool _configurationMode;
    public IReadOnlyList<DetectionTile> Tiles => _tiles;

    public DetectionDashboard() : this(false) { }

    public DetectionDashboard(bool configurationMode)
    {
        _configurationMode = configurationMode;
        _empty.Text = configurationMode ? "添加面板以配置检测图像和结果。" : "尚未配置检测显示，请返回编辑模式，在“视图”中配置。";
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        toolbar.Visibility = configurationMode ? Visibility.Visible : Visibility.Collapsed;
        SetDock(toolbar, Dock.Top); Children.Add(toolbar);
        var add = new Button { Content = "添加面板", Padding = new Thickness(12, 5, 12, 5) };
        add.Click += (_, _) => AddPanel(); toolbar.Children.Add(add);
        toolbar.Children.Add(new TextBlock { Text = "选择底图，展开“叠加节点与显示字段”组合多个节点的图形、判定和数值；拖动分隔条调整大小", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 3, 0, 3) });
        Children.Add(new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public void SetSource(FlowTabViewModel? flow, Func<WorkflowExecutionRun?> run, bool reloadPanels = false)
    {
        if (_disposed) return;
        _run = run;
        if (reloadPanels || !ReferenceEquals(_flow, flow))
        {
            foreach (var tile in _tiles) tile.Dispose(); _tiles.Clear(); _flow = flow;
            var settingsList = flow?.Document.DetectionPanels ?? [];
            var upgraded = settingsList.Select(UpgradeLegacyPanel).ToArray();
            foreach (var settings in upgraded) AddTile(settings);
            Layout();
            if (settingsList.Where((settings, index) => !ReferenceEquals(settings, upgraded[index])).Any()) Save();
        }
        foreach (var tile in _tiles) tile.Refresh();
    }

    private static DetectionPanelSettings UpgradeLegacyPanel(DetectionPanelSettings panel)
    {
        var contents = panel.Contents ?? [new(Guid.NewGuid(), panel.NodeId, panel.View, panel.OutputField)];
        var image = contents.FirstOrDefault(content => content.View == "图像");
        if (image is null || contents.Any(content => content.Overlays is not null)) return panel;
        var overlays = contents.GroupBy(content => content.NodeId).Select(group => new DetectionOverlaySettings(group.Key,
            ShowGeometry: group.Any(content => content.View == "图像"),
            ShowDecision: group.Any(content => content.View == "检测结果" || content.View == "图像"),
            Fields: group.Any(content => content.OutputField == "全部") ? null : group.Where(content => content.View != "图像")
                .Select(content => content.OutputField).Distinct().ToList())).ToList();
        return panel with { Contents = [image with { Overlays = overlays }] };
    }

    public void EnsureInitialPanels()
    {
        if (_disposed || _flow is null || _tiles.Count != 0 || _flow.Nodes.Count == 0) return;
        var source = _flow.Nodes.FirstOrDefault(node => node.TypeId is "halcon.image-read" or "halcon.camera-capture") ?? _flow.Nodes.First();
        var result = _flow.SelectedNode ?? _flow.Nodes.Last();
        AddTile(new(Guid.NewGuid(), source.Id, Contents:
            [new(Guid.NewGuid(), source.Id, Overlays: [new(result.Id)])]));
        Layout(); Save();
    }

    public void AddPanel()
    {
        if (_disposed || _flow is null) return;
        var source = _flow.Nodes.FirstOrDefault(node => node.TypeId is "halcon.image-read" or "halcon.camera-capture") ?? _flow.Nodes.FirstOrDefault();
        if (source is null) return;
        var result = _flow.SelectedNode ?? source;
        AddTile(new(Guid.NewGuid(), source.Id, Contents:
            [new(Guid.NewGuid(), source.Id, Overlays: [new(result.Id)])])); Layout(); Save();
    }

    private void AddTile(DetectionPanelSettings settings)
    {
        var tile = new DetectionTile(settings, () => _flow, () => _run(), _configurationMode);
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
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
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
        if (_tiles.Count >= 2)
        {
            var vertical = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.SlateGray, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(vertical, 1); Grid.SetRowSpan(vertical, _tiles.Count / 2 * 2 - 1); _grid.Children.Add(vertical);
        }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; foreach (var tile in _tiles) tile.Dispose(); _tiles.Clear(); _grid.Children.Clear(); }
}
