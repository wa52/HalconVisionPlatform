using System.Text.Json;

namespace HalconVision.Domain;

public sealed record ProjectDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新建项目";
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public string DefaultFlowId { get; init; } = string.Empty;
    public List<FlowDocument> Flows { get; init; } = [];
}

public sealed record FlowDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新建流程";
    public List<NodeInstance> Nodes { get; init; } = [];
    public List<FlowEdge> Edges { get; init; } = [];
    public Dictionary<string, JsonElement> Variables { get; init; } = [];
    public List<string> AssetReferences { get; init; } = [];
    public List<DetectionPanelSettings> DetectionPanels { get; init; } = [];
    public List<InspectionRegion> InspectionRegions { get; init; } = [];
    public List<InspectionTask> InspectionTasks { get; init; } = [];
}

public sealed record DetectionPanelSettings(Guid Id, Guid NodeId, string View = "图像", string OutputField = "全部",
    List<DetectionContentSettings>? Contents = null);
public sealed record DetectionContentSettings(Guid Id, Guid NodeId, string View = "图像", string OutputField = "全部",
    List<DetectionOverlaySettings>? Overlays = null, int ImageIndex = 1);
public sealed record DetectionOverlaySettings(Guid NodeId, bool ShowGeometry = true, bool ShowDecision = true,
    List<string>? Fields = null);

public sealed record NodeInstance
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TypeId { get; init; } = string.Empty;
    public int TypeVersion { get; init; } = 1;
    public string DisplayName { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public bool Enabled { get; init; } = true;
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}

public sealed record FlowEdge(Guid Id, Guid FromNodeId, string FromPortId, Guid ToNodeId, string ToPortId);

public sealed record NodePosition(double X, double Y);

public enum PortDirection { Input, Output }

public sealed record PortDefinition(string Id, string Name, PortDirection Direction, string DataType, bool Required = false, bool Multiple = false, bool Annotated = false, bool RequiresUnannotatedImage = false);

public enum NodeExecutionStatus { Idle, Running, Success, Failed, Skipped }

public enum WorkflowRunStatus { Success, Failed, Cancelled }

public sealed record NodeExecutionResult(
    Guid NodeId,
    string NodeName,
    NodeExecutionStatus Status,
    DateTime StartedAtUtc,
    DateTime EndedAtUtc,
    double ElapsedMilliseconds,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    string? Error);

public sealed record WorkflowExecutionRun(
    Guid Id,
    Guid FlowId,
    DateTime StartedAtUtc,
    DateTime EndedAtUtc,
    WorkflowRunStatus Status,
    IReadOnlyList<NodeExecutionResult> NodeResults,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Logs,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>>? RuntimeOutputs = null,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, FrameReference>>? SpatialOutputs = null,
    ProductInspectionResult? Inspection = null) : IDisposable
{
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (RuntimeOutputs is null) return;
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var value in RuntimeOutputs.Values.SelectMany(outputs => outputs.Values))
            if (value is IDisposable disposable && disposed.Add(value))
                try { disposable.Dispose(); } catch { /* Cleanup must not mask the run result. */ }
    }
}
