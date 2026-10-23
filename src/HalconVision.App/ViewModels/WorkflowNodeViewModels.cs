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
    public string Name => Definition.Name + (IsInspectionBoundRoi ? "（任务区域）" : "");
    public bool IsInspectionBoundRoi => Definition.Id == "roi" && _owner.Document.InspectionTasks.Any(t => t.NodeIds.Contains(_nodeId));
    public bool CanClearRoi => !IsInspectionBoundRoi;
    public string RoiDrawHelp => IsInspectionBoundRoi ? "编辑此节点绑定的任务区域；共用该区域的任务也会更新" : "在节点图像中拖动绘制矩形检测范围";
    public string ClearRoiHelp => IsInspectionBoundRoi ? "任务区域不能在节点中清空，请在检测任务中调整引用" : "清除已绘制范围；重新运行时按该节点的范围要求处理";
    internal void RefreshInspectionBinding()
    { Raise(nameof(Name)); Raise(nameof(IsInspectionBoundRoi)); Raise(nameof(CanClearRoi)); Raise(nameof(RoiDrawHelp)); Raise(nameof(ClearRoiHelp)); }
    public string DataType => Definition.DataType;
    public bool IsPath => DataType is "path" or "imageFile";
    public bool IsImageFile => DataType == "imageFile";
    public bool IsQuickMatchTemplate => Definition.Id == "templateImage";
    public bool IsQuickMatchTemplateRoi => Definition.Id == "templateRoi";
    public bool IsPositionReference => DataType == "positionReference";
    public string ReferenceSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ValueText)) return "未示教 · 先运行快捷匹配，再设为基准";
            try
            {
                var reference = JsonSerializer.Deserialize<PositionReference>(ValueText);
                return reference is null ? "未示教 · 先运行快捷匹配，再设为基准" :
                    $"已示教 · 行 {reference.Pose.Row:F2} · 列 {reference.Pose.Column:F2} · {reference.Pose.AngleDegrees:F2}°\n{reference.Width} × {reference.Height} · {reference.TaughtAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            }
            catch { return "基准无效 · 请重新示教"; }
        }
    }
    public bool IsModelPackage => Definition.Id == "modelPackage";
    public bool IsRoiDraw => string.Equals(DataType, "roiDraw", StringComparison.Ordinal);
    public bool IsRoiList => DataType == "roiList";
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
    public string ValueText { get => _valueText; set { if (Set(ref _valueText, value)) Raise(nameof(ReferenceSummary)); } }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public void Commit()
    {
        try
        {
            var element = Definition.DataType switch
            {
                "string" or "path" or "imageFile" or "camera" or "cameraInterface" or "roiDraw" or "lineDraw" or "edgeTransition" or "lineOrientation" or "dlDevice" => JsonSerializer.SerializeToElement(ValueText),
                "number" when double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
                "bool" when bool.TryParse(ValueText, out var boolean) => JsonSerializer.SerializeToElement(boolean),
                "roiList" => JsonSerializer.SerializeToElement(DetectionRoi.Parse(JsonSerializer.Deserialize<JsonElement>(ValueText))),
                "positionReference" => JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<PositionReference>(ValueText)),
                _ => throw new FormatException($"请输入有效的 {Definition.DataType} 值。")
            };
            _owner.SetParameter(_nodeId, Definition.Id, element);
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    public IReadOnlyList<DetectionRoi> ReadRois() => DetectionRoi.Parse(JsonSerializer.Deserialize<JsonElement>(ValueText));
    public void SetRois(IEnumerable<DetectionRoi> rois)
    {
        ValueText = JsonSerializer.Serialize(rois); Commit();
        if (Error is not null) throw new InvalidDataException(Error);
    }
    public void SaveDrawnRoi(Guid? id, double row1, double col1, double row2, double col2)
    {
        var rois = ReadRois().ToList();
        if (id is { } selected)
        {
            var index = rois.FindIndex(item => item.Id == selected);
            if (index < 0) throw new InvalidOperationException("所选 ROI 已删除，请重新选择。");
            rois[index] = rois[index] with { RowBegin = row1, ColumnBegin = col1, RowEnd = row2, ColumnEnd = col2 };
        }
        else
        {
            if (rois.Count >= DetectionRoi.MaximumCount) throw new InvalidDataException($"最多支持 {DetectionRoi.MaximumCount} 个 ROI。");
            var number = 1;
            while (rois.Any(item => string.Equals(item.Name, $"ROI {number}", StringComparison.OrdinalIgnoreCase))) number++;
            rois.Add(new(Guid.NewGuid(), $"ROI {number}", row1, col1, row2, col2));
        }
        SetRois(rois);
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
                ParameterValue(node, parameter))));
    }

    private static JsonElement ParameterValue(NodeInstance node, NodeParameterDefinition parameter)
    {
        if (node.Parameters.TryGetValue(parameter.Id, out var value)) return value;
        if (parameter.DataType == "roiList" && node.Parameters.TryGetValue("roi", out var legacy) && legacy.ValueKind == JsonValueKind.String)
        {
            var tokens = (legacy.GetString() ?? "").Split(',');
            var numbers = tokens.Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN).ToArray();
            if (numbers.Length == 4 && numbers.All(double.IsFinite))
                return JsonSerializer.SerializeToElement(new[] { new DetectionRoi(Guid.NewGuid(), "ROI 1", numbers[0], numbers[1], numbers[2], numbers[3]) });
        }
        return parameter.DefaultValue ?? JsonSerializer.SerializeToElement(string.Empty);
    }

    public Guid Id { get; }
    public string TypeId { get; }
    public int TypeVersion { get; }
    public NodeDefinition? Definition { get; }
    public string Category => Definition?.Category ?? "缺少节点定义";
    public ObservableCollection<NodePortViewModel> InputPorts { get; }
    public ObservableCollection<NodePortViewModel> OutputPorts { get; }
    public ObservableCollection<NodeParameterViewModel> Parameters { get; }
    private static readonly HashSet<string> QuickMatchBasic = ["templateRoi", "roi", "minimumScore", "maximumMatches"];
    public IEnumerable<NodeParameterViewModel> PrimaryParameters => TypeId == HalconNodeProvider.QuickMatch
        ? Parameters.Where(p => QuickMatchBasic.Contains(p.Definition.Id)) : Parameters.Where(p => !p.Definition.Advanced);
    public IEnumerable<NodeParameterViewModel> AdvancedParameters => TypeId == HalconNodeProvider.QuickMatch
        ? Parameters.Where(p => !QuickMatchBasic.Contains(p.Definition.Id) && !p.IsQuickMatchTemplate) : Parameters.Where(p => p.Definition.Advanced);
    public bool HasAdvancedParameters => AdvancedParameters.Any();
    public string AdvancedTitle => TypeId == HalconNodeProvider.QuickMatch ? "高级匹配参数" : "高级测量参数";
    public string AdvancedHelp => TypeId == HalconNodeProvider.QuickMatch
        ? "展开候选搜索、歧义判定、允许位置及搜索超时参数" : "展开测量卡尺、半径范围、残差校验和诊断显示参数";
    public double X { get => _x; private set => Set(ref _x, value); }
    public double Y { get => _y; private set => Set(ref _y, value); }
    public string DisplayName { get => _displayName; set { if (Set(ref _displayName, value)) _owner.RenameNode(Id, value); } }
    public bool Enabled { get; private set; }
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }
    public string Status { get => _status; internal set => Set(ref _status, value); }
    internal void MoveTo(double x, double y) { X = Math.Max(0, x); Y = Math.Max(0, y); }
}

public sealed record FlowEdgeViewModel(double X1, double Y1, double X2, double Y2, string State);
