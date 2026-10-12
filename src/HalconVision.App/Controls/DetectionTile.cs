using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed class DetectionTile : Border, IDisposable
{
    private readonly Guid _id;
    private readonly Func<FlowTabViewModel?> _flow;
    private readonly Func<WorkflowExecutionRun?> _run;
    private readonly Grid _grid = new();
    private readonly List<DetectionContentView> _contents = [];
    private bool _disposed;
    public IReadOnlyList<DetectionContentView> Contents => _contents;
    public DetectionPanelSettings Settings
    {
        get
        {
            var first = _contents.FirstOrDefault()?.Settings;
            return new(_id, first?.NodeId ?? Guid.Empty, first?.View ?? "图像", first?.OutputField ?? "全部",
                _contents.Select(content => content.Settings).ToList());
        }
    }
    public event Action<DetectionTile>? Changed;
    public event Action<DetectionTile>? RemoveRequested;

    public DetectionTile(DetectionPanelSettings settings, Func<FlowTabViewModel?> flow, Func<WorkflowExecutionRun?> run)
    {
        _id = settings.Id; _flow = flow; _run = run;
        Margin = new Thickness(3); BorderThickness = new Thickness(2); BorderBrush = Brushes.DarkCyan;
        Background = new SolidColorBrush(Color.FromRgb(27, 39, 49));
        var root = new DockPanel(); Child = root;
        var header = new WrapPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "组合面板", Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) });
        var add = new Button { Content = "添加内容", Padding = new Thickness(10, 4, 10, 4) };
        add.Click += (_, _) => AddContent(); header.Children.Add(add);
        var remove = new Button { Content = "删除面板", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        remove.Click += (_, _) => RemoveRequested?.Invoke(this); header.Children.Add(remove);
        header.Children.Add(new TextBlock { Text = "同一个面板内，每项内容可选不同节点和字段", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        root.Children.Add(_grid);
        foreach (var content in settings.Contents ?? [new(Guid.NewGuid(), settings.NodeId, settings.View, settings.OutputField)]) AddView(content);
        Layout();
    }

    public void AddContent()
    {
        if (_disposed) return;
        var node = _flow()?.SelectedNode ?? _flow()?.Nodes.FirstOrDefault();
        if (node is null) return;
        AddView(new(Guid.NewGuid(), node.Id)); Layout(); Changed?.Invoke(this);
    }

    public void RemoveContent(DetectionContentView content)
    {
        if (!_contents.Remove(content)) return;
        content.Dispose(); Layout(); Changed?.Invoke(this);
    }

    private void AddView(DetectionContentSettings settings)
    {
        var content = new DetectionContentView(settings, _flow, _run);
        content.Changed += _ => Changed?.Invoke(this); content.RemoveRequested += RemoveContent;
        _contents.Add(content);
    }

    private void Layout()
    {
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (_contents.Count == 0)
        {
            var empty = new TextBlock { Text = "点击“添加内容”，在此面板组合不同节点的内容。", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumnSpan(empty, 3); _grid.Children.Add(empty); return;
        }
        var rows = (_contents.Count + 1) / 2;
        for (var row = 0; row < rows; row++)
        {
            if (row > 0)
            {
                _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
                var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.SlateGray, ResizeDirection = GridResizeDirection.Rows };
                Grid.SetRow(splitter, row * 2 - 1); Grid.SetColumnSpan(splitter, 3); _grid.Children.Add(splitter);
            }
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
        }
        for (var index = 0; index < _contents.Count; index++)
        {
            var content = _contents[index]; Grid.SetRow(content, index / 2 * 2); Grid.SetColumn(content, index % 2 * 2);
            Grid.SetColumnSpan(content, index == _contents.Count - 1 && _contents.Count % 2 == 1 ? 3 : 1);
            _grid.Children.Add(content);
        }
        if (_contents.Count >= 2)
        {
            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.SlateGray, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(splitter, 1); Grid.SetRowSpan(splitter, _contents.Count / 2 * 2 - 1); _grid.Children.Add(splitter);
        }
    }

    public void Refresh() { if (!_disposed) foreach (var content in _contents) content.Refresh(); }
    public void Dispose() { if (_disposed) return; _disposed = true; foreach (var content in _contents) content.Dispose(); _contents.Clear(); _grid.Children.Clear(); }
}
