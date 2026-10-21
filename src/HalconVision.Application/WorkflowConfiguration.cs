using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public static class WorkflowConfiguration
{
    // Topology and algorithm settings, excluding labels/positions and identity-only fields.
    // Excluding node IDs makes a complete copied/imported flow retain the same calibration identity.
    public static string NodeSignature(FlowDocument flow, Guid nodeId)
    {
        var seen = new HashSet<Guid>();
        string Visit(Guid id)
        {
            if (!seen.Add(id)) throw new InvalidDataException("配置依赖存在循环。");
            var node = flow.Nodes.Single(item => item.Id == id);
            var parameters = node.Parameters.OrderBy(p => p.Key).Select(p => new { key = p.Key, value = CanonicalParameter(node, p.Key, p.Value) });
            var inputs = flow.Edges.Where(e => e.ToNodeId == id).OrderBy(e => e.ToPortId).ThenBy(e => e.FromPortId)
                .Select(e => new { input = e.ToPortId, output = e.FromPortId, signature = Visit(e.FromNodeId) }).ToArray();
            var task = flow.InspectionTasks.FirstOrDefault(t => t.NodeIds.Contains(id));
            var region = task is null ? null : flow.InspectionRegions.FirstOrDefault(r => r.Id == task.RegionId);
            var taskRegion = region is null ? null : new { region.Bounds, region.ImagePortId, source = Visit(region.ImageNodeId), enabled = task!.Enabled };
            var text = taskRegion is null
                ? JsonSerializer.Serialize(new { node.TypeId, node.TypeVersion, node.Enabled, parameters = parameters.ToArray(), inputs })
                : JsonSerializer.Serialize(new { node.TypeId, node.TypeVersion, node.Enabled, parameters = parameters.ToArray(), inputs, taskRegion });
            seen.Remove(id);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
        return Visit(nodeId);
    }

    private static JsonElement CanonicalParameter(NodeInstance node, string key, JsonElement value)
    {
        if (node.TypeId != "halcon.position-correction" || key != PositionReferenceParameters.Key || value.ValueKind != JsonValueKind.Object) return value;
        var reference = value.Deserialize<PositionReference>() ?? throw new InvalidDataException("基准数据无效。");
        // Retaught geometry invalidates downstream references; copying IDs and timestamps do not.
        return JsonSerializer.SerializeToElement(new { reference.Pose, reference.Width, reference.Height, reference.ModelIdentity,
            reference.ImageConfigurationIdentity, reference.PoseImageConfigurationIdentity });
    }
}
