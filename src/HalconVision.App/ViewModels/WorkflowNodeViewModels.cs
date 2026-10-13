using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

namespace HalconVision.App;

public sealed class ProjectTreeItem(string name, string relativePath, bool isDirectory) : ObservableModel
{
    public string Name { get; } = name;
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = isDirectory;
    public ObservableCollection<ProjectTreeItem> Children { get; } = [];
}

public sealed class NodePortViewModel(Guid nodeId, string id, string name, PortDirection direction, string dataType, bool required) : ObservableModel
{
    public Guid NodeId { get; } = nodeId;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public PortDirection Direction { get; } = direction;
    public string DataType { get; } = dataType;
    public bool Required { get; } = required;
}

public sealed class NodeParameterViewModel : ObservableModel
{
    private string _valueText;
    private string? _error;
    private readonly FlowTabViewModel _owner;
    private readonly Guid _nodeId;

    public NodeParameterViewModel(FlowTabViewModel owner, Guid nodeId, NodeParameterDefinition definition, JsonElement value)
    {
        _owner = owner; _nodeId = nodeId; Definition = definition;
        _valueText = Format(value);
    }

    public NodeParameterDefinition Definition { get; }
    public string Name => Definition.Name;
    public string DataType => Definition.DataType;
    public bool IsPath => string.Equals(DataType, "path", StringComparison.Ordinal);
    public bool IsModelPackage => Definition.Id == "modelPackage";
    public bool IsRoiDraw => string.Equals(DataType, "roiDraw", StringComparison.Ordinal);
    public bool IsLineDraw => string.Equals(DataType, "lineDraw", StringComparison.Ordinal);
    public bool IsBoolean => string.Equals(DataType, "bool", StringComparison.Ordinal);
    public bool IsTransition => DataType is "edgeTransition" or "lineOrientation" or "dlDevice";
    public IReadOnlyList<string> TransitionChoices => DataType == "dlDevice" ? ["GPU", "CPU"] : DataType == "lineOrientation"
        ? ["自动（矩形长边方向）", "水平直线", "垂直直线"] : ["全部边缘", "暗到亮", "亮到暗"];
    public string SelectedTransition
    {
        get => DataType == "dlDevice" ? ValueText.ToUpperInvariant() : DataType == "lineOrientation"
            ? ValueText switch { "horizontal" => "水平直线", "vertical" => "垂直直线", _ => "自动（矩形长边方向）" }
            : ValueText switch { "positive" => "暗到亮", "negative" => "亮到暗", _ => "全部边缘" };
        set { ValueText = value switch { "GPU" => "gpu", "CPU" => "cpu", "水平直线" => "horizontal", "垂直直线" => "vertical", "自动（矩形长边方向）" => "auto", "暗到亮" => "positive", "亮到暗" => "negative", _ => "all" }; Commit(); }
    }
    public bool BooleanValue
    {
        get => bool.TryParse(ValueText, out var value) && value;
        set { ValueText = value ? "true" : "false"; Commit(); }
    }
    public bool IsCamera => DataType is "camera" or "cameraInterface";
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public void Commit()
    {
        try
        {
            var element = Definition.DataType switch
            {
                "string" or "path" or "camera" or "cameraInterface" or "roiDraw" or "lineDraw" or "edgeTransition" or "lineOrientation" or "dlDevice" => JsonSerializer.SerializeToElement(ValueText),
                "number" when double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
                "bool" when bool.TryParse(ValueText, out var boolean) => JsonSerializer.SerializeToElement(boolean),
                _ => throw new FormatException($"请输入有效的 {Definition.DataType} 值。")
            };
            _owner.SetParameter(_nodeId, Definition.Id, element);
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
        _ => value.GetRawText()
    };
}

public sealed class FlowNodeViewModel : ObservableModel
{
    private double _x;
    private double _y;
    private string _displayName;
    private bool _isSelected;
    private string _status = "Idle";
    private readonly FlowTabViewModel _owner;

    public FlowNodeViewModel(FlowTabViewModel owner, NodeInstance node, NodeDefinition? definition)
    {
        _owner = owner; Id = node.Id; TypeId = node.TypeId; TypeVersion = node.TypeVersion;
        _displayName = node.DisplayName; _x = node.X; _y = node.Y; Enabled = node.Enabled;
        Definition = definition;
        InputPorts = new ObservableCollection<NodePortViewModel>((definition?.Ports ?? []).Where(port => port.Direction == PortDirection.Input)
            .Select(port => new NodePortViewModel(Id, port.Id, port.Name, port.Direction, port.DataType, port.Required)));
        OutputPorts = new ObservableCollection<NodePortViewModel>((definition?.Ports ?? []).Where(port => port.Direction == PortDirection.Output)
            .Select(port => new NodePortViewModel(Id, port.Id, port.Name, port.Direction, port.DataType, port.Required)));
        Parameters = new ObservableCollection<NodeParameterViewModel>((definition?.Parameters ?? []).Select(parameter =>
            new NodeParameterViewModel(owner, Id, parameter,
                node.Parameters.TryGetValue(parameter.Id, out var value) ? value : parameter.DefaultValue ?? JsonSerializer.SerializeToElement(string.Empty))));
    }

    public Guid Id { get; }
    public string TypeId { get; }
    public int TypeVersion { get; }
    public NodeDefinition? Definition { get; }
    public string Category => Definition?.Category ?? "缺少节点定义";
    public ObservableCollection<NodePortViewModel> InputPorts { get; }
    public ObservableCollection<NodePortViewModel> OutputPorts { get; }
    public ObservableCollection<NodeParameterViewModel> Parameters { get; }
    public double X { get => _x; private set => Set(ref _x, value); }
    public double Y { get => _y; private set => Set(ref _y, value); }
    public string DisplayName { get => _displayName; set { if (Set(ref _displayName, value)) _owner.RenameNode(Id, value); } }
    public bool Enabled { get; private set; }
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }
    public string Status { get => _status; internal set => Set(ref _status, value); }
    internal void MoveTo(double x, double y) { X = Math.Max(0, x); Y = Math.Max(0, y); }
}

public sealed record FlowEdgeViewModel(double X1, double Y1, double X2, double Y2, string State);
