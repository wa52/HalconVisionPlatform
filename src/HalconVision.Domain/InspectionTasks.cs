using System.Text.Json;

namespace HalconVision.Domain;

public sealed record InspectionRegion(Guid Id, string Name, Guid ImageNodeId, string ImagePortId, Rectangle2D Bounds);
public enum InspectionRuleKind { NumberRange, TextEquals, BooleanTrue, DetectionDecision, MatchStatus }
public sealed record InspectionRule(Guid Id, Guid NodeId, string PortId, InspectionRuleKind Kind,
    string Pointer = "", double Minimum = 0, double Maximum = 1, string Expected = "");
public sealed record InspectionTask(Guid Id, string Name, Guid RegionId, List<Guid> NodeIds,
    List<InspectionRule> Rules, bool Required = true, bool Enabled = true);
public enum InspectionVerdict { NotConfigured, Ok, Ng, Error, Skipped }
public sealed record InspectionTaskResult(Guid TaskId, string Name, bool Required, InspectionVerdict Verdict, IReadOnlyList<string> Reasons);
public sealed record ProductInspectionResult(Guid RunId, Guid FlowId, InspectionVerdict Verdict,
    IReadOnlyList<InspectionTaskResult> Tasks, IReadOnlyList<string> Reasons, string ConfigurationIdentity = "")
{
    public string Label => Verdict switch { InspectionVerdict.Ok => "OK", InspectionVerdict.Ng => "NG", InspectionVerdict.Error => "ERROR", _ => "未配置产品判定" };
}

public static class InspectionTaskReferences
{
    public static FlowDocument Remap(FlowDocument source, FlowDocument target, IReadOnlyDictionary<Guid, Guid> nodeIds)
    {
        var regionIds = source.InspectionRegions.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        return target with
        {
            InspectionRegions = source.InspectionRegions.Select(r => r with { Id = regionIds[r.Id], ImageNodeId = nodeIds[r.ImageNodeId] }).ToList(),
            InspectionTasks = source.InspectionTasks.Select(t => t with { Id = Guid.NewGuid(), RegionId = regionIds[t.RegionId],
                NodeIds = t.NodeIds.Select(id => nodeIds[id]).ToList(), Rules = t.Rules.Select(r => r with { Id = Guid.NewGuid(), NodeId = nodeIds[r.NodeId] }).ToList() }).ToList()
        };
    }
}
