using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public sealed class WorkflowGraphValidator(INodeRegistry registry)
{
    public IReadOnlyList<WorkflowValidationIssue> Validate(FlowDocument flow) => Analyze(flow).Issues;

    public IReadOnlyList<NodeInstance> GetExecutionOrder(FlowDocument flow)
    {
        var analysis = Analyze(flow);
        if (analysis.Issues.Count > 0) throw new WorkflowValidationException(analysis.Issues);
        return analysis.Order;
    }

    private GraphAnalysis Analyze(FlowDocument flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var issues = new List<WorkflowValidationIssue>();
        var byId = new Dictionary<Guid, NodeInstance>();
        foreach (var node in flow.Nodes)
        {
            if (!byId.TryAdd(node.Id, node))
                issues.Add(new WorkflowValidationIssue("流程中存在重复的节点 ID。", node.Id));
            if (!registry.TryGetDefinition(node.TypeId, node.TypeVersion, out var definition))
            {
                issues.Add(new WorkflowValidationIssue($"节点定义不可用：{node.TypeId}@{node.TypeVersion}。", node.Id));
                continue;
            }
            foreach (var parameter in definition.Parameters)
            {
                if (!node.Parameters.TryGetValue(parameter.Id, out var value))
                {
                    if (parameter.Required && parameter.DefaultValue is null)
                        issues.Add(new WorkflowValidationIssue($"缺少必填参数“{parameter.Name}”。", node.Id));
                    continue;
                }
                if (parameter.Required && value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                    issues.Add(new WorkflowValidationIssue($"必填参数“{parameter.Name}”不能为空。", node.Id));
                if (!MatchesDataType(value, parameter.DataType))
                    issues.Add(new WorkflowValidationIssue($"参数“{parameter.Name}”类型无效，应为 {parameter.DataType}。", node.Id));
            }
        }

        var edgeIds = new HashSet<Guid>();
        var duplicateEdges = new HashSet<(Guid, string, Guid, string)>();
        var incoming = new Dictionary<(Guid, string), int>();
        var adjacency = byId.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var edge in flow.Edges)
        {
            if (!edgeIds.Add(edge.Id)) issues.Add(new WorkflowValidationIssue("流程中存在重复的连线 ID。", EdgeId: edge.Id));
            if (!duplicateEdges.Add((edge.FromNodeId, edge.FromPortId, edge.ToNodeId, edge.ToPortId)))
                issues.Add(new WorkflowValidationIssue("存在重复连线。", EdgeId: edge.Id));
            if (!byId.TryGetValue(edge.FromNodeId, out var source) || !byId.TryGetValue(edge.ToNodeId, out var target))
            {
                issues.Add(new WorkflowValidationIssue("连线引用了不存在的节点。", EdgeId: edge.Id));
                continue;
            }
            if (!registry.TryGetDefinition(source.TypeId, source.TypeVersion, out var sourceDefinition) ||
                !registry.TryGetDefinition(target.TypeId, target.TypeVersion, out var targetDefinition)) continue;

            var sourcePort = sourceDefinition.Ports.SingleOrDefault(port => port.Id == edge.FromPortId);
            var targetPort = targetDefinition.Ports.SingleOrDefault(port => port.Id == edge.ToPortId);
            if (sourcePort is null || sourcePort.Direction != PortDirection.Output)
            {
                issues.Add(new WorkflowValidationIssue("连线的起点端口不存在或不是输出端口。", source.Id, edge.Id));
                continue;
            }
            if (targetPort is null || targetPort.Direction != PortDirection.Input)
            {
                issues.Add(new WorkflowValidationIssue("连线的终点端口不存在或不是输入端口。", target.Id, edge.Id));
                continue;
            }
            if (sourcePort.DataType != "any" && targetPort.DataType != "any" && sourcePort.DataType != targetPort.DataType)
                issues.Add(new WorkflowValidationIssue($"端口类型不匹配：{sourcePort.DataType} → {targetPort.DataType}。", target.Id, edge.Id));
            incoming[(target.Id, targetPort.Id)] = incoming.GetValueOrDefault((target.Id, targetPort.Id)) + 1;
            if (!targetPort.Multiple && incoming[(target.Id, targetPort.Id)] > 1)
                issues.Add(new WorkflowValidationIssue($"输入端口“{targetPort.Name}”只允许一条连线。", target.Id, edge.Id));
            adjacency[source.Id].Add(target.Id);
        }

        foreach (var node in flow.Nodes)
        {
            if (!registry.TryGetDefinition(node.TypeId, node.TypeVersion, out var definition)) continue;
            foreach (var required in definition.Ports.Where(port => port.Direction == PortDirection.Input && port.Required))
                if (incoming.GetValueOrDefault((node.Id, required.Id)) == 0)
                    issues.Add(new WorkflowValidationIssue($"必需输入端口“{required.Name}”未连接。", node.Id));
        }

        var indegree = byId.Keys.ToDictionary(id => id, _ => 0);
        foreach (var targets in adjacency.Values)
            foreach (var target in targets) indegree[target]++;
        var ready = new Queue<NodeInstance>(flow.Nodes.Where(node => indegree.GetValueOrDefault(node.Id) == 0));
        var order = new List<NodeInstance>(flow.Nodes.Count);
        while (ready.TryDequeue(out var node))
        {
            order.Add(node);
            foreach (var next in adjacency[node.Id])
            {
                indegree[next]--;
                if (indegree[next] == 0) ready.Enqueue(byId[next]);
            }
        }
        if (order.Count != byId.Count)
            issues.Add(new WorkflowValidationIssue("流程包含有向环，无法执行。"));
        return new GraphAnalysis(issues, order);
    }

    private static bool MatchesDataType(JsonElement value, string type) => type switch
    {
        "any" => true,
        "string" or "path" or "camera" or "cameraInterface" or "roiDraw" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        _ => false
    };

    private sealed record GraphAnalysis(IReadOnlyList<WorkflowValidationIssue> Issues, IReadOnlyList<NodeInstance> Order);
}

public sealed class WorkflowValidationException(IReadOnlyList<WorkflowValidationIssue> issues)
    : InvalidOperationException(string.Join(Environment.NewLine, issues.Select(issue => issue.Message)))
{
    public IReadOnlyList<WorkflowValidationIssue> Issues { get; } = issues;
}
