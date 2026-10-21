using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HalconVision.Domain;

namespace HalconVision.Application;

public static class InspectionPlan
{
    public static string ConfigurationIdentity(FlowDocument flow)
    {
        try
        {
            var value = new { flow.Id, nodes = flow.Nodes.Select(n => new { n.Id, signature = WorkflowConfiguration.NodeSignature(flow, n.Id) }), flow.Edges, flow.InspectionRegions, flow.InspectionTasks };
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
        }
        catch { return "INVALID"; }
    }
    public static IReadOnlyList<WorkflowValidationIssue> Validate(FlowDocument flow, INodeRegistry registry)
    {
        var issues = new List<WorkflowValidationIssue>();
        void Reject(string message) => issues.Add(new("检测任务：" + message));
        try
        {
            if (flow.InspectionRegions.Count > 256 || flow.InspectionTasks.Count > 256) Reject("区域或任务数量超过 256。");
            if (flow.InspectionRegions.Select(r => r.Id).Distinct().Count() != flow.InspectionRegions.Count ||
                flow.InspectionTasks.Select(t => t.Id).Distinct().Count() != flow.InspectionTasks.Count) Reject("区域或任务 ID 重复。");
            if (flow.InspectionRegions.Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != flow.InspectionRegions.Count ||
                flow.InspectionTasks.Select(t => t.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != flow.InspectionTasks.Count) Reject("区域或任务名称重复。");
            var owners = new HashSet<Guid>(); var ruleIds = new HashSet<Guid>();
            foreach (var region in flow.InspectionRegions)
            {
                if (region.Id == Guid.Empty || string.IsNullOrWhiteSpace(region.Name)) Reject("区域名称或 ID 无效。");
                var b = region.Bounds;
                if (b is null || !new[] { b.RowBegin, b.ColumnBegin, b.RowEnd, b.ColumnEnd }.All(double.IsFinite) ||
                    b.RowBegin < 0 || b.ColumnBegin < 0 || b.RowEnd <= b.RowBegin || b.ColumnEnd <= b.ColumnBegin) Reject($"区域 {region.Name} 的矩形坐标无效。");
                var source = flow.Nodes.FirstOrDefault(n => n.Id == region.ImageNodeId);
                if (flow.InspectionTasks.Any(t => t.NodeIds.Contains(region.ImageNodeId))) Reject("区域图像来源必须位于任务分支之外。");
                if (source is null || !registry.TryGetDefinition(source.TypeId, source.TypeVersion, out var definition) ||
                    !definition.Ports.Any(p => p.Id == region.ImagePortId && p.Direction == PortDirection.Output && p.DataType == "halcon.image" && !p.Annotated))
                    Reject($"区域 {region.Name} 必须绑定未标注图像输出。");
            }
            foreach (var task in flow.InspectionTasks)
            {
                if (task.Id == Guid.Empty || string.IsNullOrWhiteSpace(task.Name)) Reject("任务名称或 ID 无效。");
                if (!task.Enabled && task.Required) Reject($"必检任务 {task.Name} 不能禁用。");
                if (!task.Enabled) continue;
                var region = flow.InspectionRegions.FirstOrDefault(r => r.Id == task.RegionId);
                if (region is null) { Reject($"任务 {task.Name} 缺少区域。"); continue; }
                if (task.NodeIds.Count == 0 || task.Rules.Count == 0 || task.NodeIds.Distinct().Count() != task.NodeIds.Count) Reject($"任务 {task.Name} 缺少节点、规则或节点重复。");
                foreach (var id in task.NodeIds)
                {
                    if (!owners.Add(id)) Reject("同一算法节点不能同时属于多个任务，请复制算法分支。");
                    var node = flow.Nodes.FirstOrDefault(n => n.Id == id);
                    if (node is null || !node.Enabled || !registry.TryGetDefinition(node.TypeId, node.TypeVersion, out var definition) ||
                        definition.TaskRoiParameterId is null && !definition.Parameters.Any(p => p.DataType == "roiList" || p.Id == "roi" && p.DataType == "roiDraw")) { Reject($"任务 {task.Name} 包含缺失、禁用或不支持区域限定的节点。"); continue; }
                    var path = new HashSet<Guid>();
                    bool ReachesSource(Guid current)
                    {
                        if (current == region.ImageNodeId) return true;
                        if (!task.NodeIds.Contains(current) || !path.Add(current)) return false;
                        return flow.Edges.Where(e => e.ToNodeId == current).Any(e => ReachesSource(e.FromNodeId));
                    }
                    if (!ReachesSource(id)) Reject($"任务 {task.Name} 的节点未连接所绑定的图像来源。");
                    foreach (var edge in flow.Edges.Where(e => e.ToNodeId == id))
                    {
                        var port = definition.Ports.FirstOrDefault(p => p.Direction == PortDirection.Input && p.Id == edge.ToPortId);
                        if (port?.DataType is not ("halcon.image" or "halcon.region")) continue;
                        if (!task.NodeIds.Contains(edge.FromNodeId) && (edge.FromNodeId != region.ImageNodeId || edge.FromPortId != region.ImagePortId))
                            Reject($"任务 {task.Name} 接入了其他图像分支。");
                    }
                }
                foreach (var rule in task.Rules)
                {
                    var node = flow.Nodes.FirstOrDefault(n => n.Id == rule.NodeId);
                    if (!ruleIds.Add(rule.Id) || rule.Id == Guid.Empty || !task.NodeIds.Contains(rule.NodeId) || node is null ||
                        !registry.TryGetDefinition(node.TypeId, node.TypeVersion, out var definition) ||
                        !definition.Ports.Any(p => p.Id == rule.PortId && p.Direction == PortDirection.Output)) Reject($"任务 {task.Name} 的规则来源无效。");
                    if (!Enum.IsDefined(rule.Kind) || rule.Pointer is null || rule.Pointer.Length > 0 && !rule.Pointer.StartsWith('/')) Reject("规则方式或字段路径无效。");
                    if (rule.Kind == InspectionRuleKind.NumberRange && (!double.IsFinite(rule.Minimum) || !double.IsFinite(rule.Maximum) || rule.Minimum > rule.Maximum)) Reject("规则上下限无效。");
                    if (rule.Kind == InspectionRuleKind.TextEquals && string.IsNullOrWhiteSpace(rule.Expected)) Reject("文本规则必须指定期望值。");
                }
                foreach (var terminal in task.NodeIds.Where(id => !flow.Edges.Any(e => e.FromNodeId == id && task.NodeIds.Contains(e.ToNodeId))))
                    if (!task.Rules.Any(r => r.NodeId == terminal)) Reject($"任务 {task.Name} 的末端算法缺少判定规则。");
            }
            // Disabled optional tasks retain ownership so their nodes cannot execute in another task.
            foreach (var task in flow.InspectionTasks.Where(t => !t.Enabled))
                foreach (var id in task.NodeIds) if (!owners.Add(id)) Reject("禁用任务与其他任务共用节点。");
        }
        catch (Exception error) { Reject("任务配置损坏：" + error.Message); }
        return issues;
    }

    public static NodeInstance Bind(FlowDocument flow, NodeInstance node, NodeDefinition definition)
    {
        var task = flow.InspectionTasks.FirstOrDefault(t => t.NodeIds.Contains(node.Id));
        if (task is null) return node;
        if (!task.Enabled) return node with { Enabled = false };
        var region = flow.InspectionRegions.Single(r => r.Id == task.RegionId);
        var parameter = definition.Parameters.FirstOrDefault(p => p.DataType == "roiList")
            ?? definition.Parameters.FirstOrDefault(p => p.Id == "roi" && p.DataType == "roiDraw");
        var parameterId = parameter?.Id ?? definition.TaskRoiParameterId ?? throw new InvalidDataException("节点不支持区域限定。");
        var b = region.Bounds;
        var value = parameter?.DataType == "roiList"
            ? JsonSerializer.SerializeToElement(new[] { new DetectionRoi(region.Id, region.Name, b.RowBegin, b.ColumnBegin, b.RowEnd, b.ColumnEnd) })
            : JsonSerializer.SerializeToElement(string.Join(",", new[] { b.RowBegin, b.ColumnBegin, b.RowEnd, b.ColumnEnd }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
        return node with { Parameters = new(node.Parameters) { [parameterId] = value } };
    }

    public static void RequireFrame(FlowDocument flow, Guid nodeId, IReadOnlyDictionary<string, FrameReference> inputs,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, FrameReference>> outputs, Guid runId)
    {
        var task = flow.InspectionTasks.FirstOrDefault(t => t.Enabled && t.NodeIds.Contains(nodeId));
        if (task is null) return;
        var region = flow.InspectionRegions.Single(r => r.Id == task.RegionId);
        var frame = outputs.GetValueOrDefault(region.ImageNodeId)?.GetValueOrDefault(region.ImagePortId)
            ?? throw new InvalidDataException("TASK_FRAME_REQUIRED：区域图像来源本轮未完成。");
        if (frame.RunId != runId || frame.Annotated || frame.Count != 1 || frame.ImageIndex != 1 || inputs.Count == 0 ||
            inputs.TryGetValue("image", out var inputImage) && inputImage.Count != 1 || inputs.Values.Any(f =>
            f.RunId != runId || f.FrameId != frame.FrameId || f.SpaceId != frame.SpaceId || f.ImageIndex != 1 || f.Annotated || f.Width != frame.Width || f.Height != frame.Height))
            throw new InvalidDataException("TASK_FRAME_MISMATCH：任务必须使用同一帧未标注的单幅图像和坐标空间。");
        if (region.Bounds.RowEnd > frame.Height - 1 || region.Bounds.ColumnEnd > frame.Width - 1)
            throw new InvalidDataException("TASK_ROI_OUT_OF_BOUNDS：任务区域超出图像。");
    }
}
