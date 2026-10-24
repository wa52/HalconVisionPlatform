using System.Diagnostics;
using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public sealed class WorkflowExecutionEngine(INodeRegistry registry, WorkflowGraphValidator validator)
{
    public async Task<WorkflowExecutionRun> RunAsync(
        FlowDocument flow,
        IProgress<NodeExecutionResult>? progress = null,
        CancellationToken cancellationToken = default,
        FlowDocument? configurationFlow = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var id = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var results = new Dictionary<Guid, NodeExecutionResult>();
        var runtimeOutputs = new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();
        var spatialOutputs = new Dictionary<Guid, IReadOnlyDictionary<string, FrameReference>>();
        var logs = new List<string>();
        var errors = new List<string>();
        ProductInspectionResult Inspect(WorkflowRunStatus status, IReadOnlyList<NodeExecutionResult> nodeResults) =>
            ProductInspectionEvaluator.Evaluate(flow, id, status, nodeResults, errors) with { ConfigurationIdentity = InspectionPlan.ConfigurationIdentity(configurationFlow ?? flow) };

        var issues = validator.Validate(flow);
        if (issues.Count > 0)
        {
            errors.AddRange(issues.Select(issue => issue.NodeId is { } nodeId
                ? $"节点 {nodeId}: {issue.Message}"
                : issue.Message));
            var failedAt = DateTime.UtcNow;
            return new WorkflowExecutionRun(id, flow.Id, startedAt, failedAt, WorkflowRunStatus.Failed, [], errors, logs,
                Inspection: Inspect(WorkflowRunStatus.Failed, []));
        }

        IReadOnlyList<NodeInstance> order;
        try { order = validator.GetExecutionOrder(flow); }
        catch (WorkflowValidationException ex)
        {
            errors.AddRange(ex.Issues.Select(issue => issue.Message));
            return new WorkflowExecutionRun(id, flow.Id, startedAt, DateTime.UtcNow, WorkflowRunStatus.Failed, [], errors, logs,
                Inspection: Inspect(WorkflowRunStatus.Failed, []));
        }

        var runtimeOwnershipTransferred = false;
        try
        {
        foreach (var originalNode in order)
        {
            var node = registry.TryGetDefinition(originalNode.TypeId, originalNode.TypeVersion, out var bindingDefinition)
                ? InspectionPlan.Bind(flow, originalNode, bindingDefinition) : originalNode;
            if (cancellationToken.IsCancellationRequested) break;
            var nodeStarted = DateTime.UtcNow;
            var timer = Stopwatch.StartNew();
            var definition = registry.TryGetDefinition(node.TypeId, node.TypeVersion, out var foundDefinition) ? foundDefinition : null;
            var pending = new NodeExecutionResult(node.Id, node.DisplayName, NodeExecutionStatus.Running, nodeStarted, nodeStarted, 0, EmptyOutputs(), null);
            progress?.Report(pending);

            NodeExecutionStatus status;
            IReadOnlyDictionary<string, JsonElement> outputs = EmptyOutputs();
            string? error = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failedDependency = flow.Edges
                    .Where(edge => edge.ToNodeId == node.Id)
                    .Select(edge => results.GetValueOrDefault(edge.FromNodeId))
                    .FirstOrDefault(result => result is null || result.Status is NodeExecutionStatus.Failed or NodeExecutionStatus.Skipped);
                if (!node.Enabled)
                {
                    status = NodeExecutionStatus.Skipped;
                    error = "节点已禁用。";
                }
                else if (failedDependency is not null)
                {
                    status = NodeExecutionStatus.Skipped;
                    error = "上游节点未成功，已跳过执行。";
                }
                else if (!registry.TryGetExecutor(node.TypeId, node.TypeVersion, out var executor) || definition is null)
                {
                    status = NodeExecutionStatus.Failed;
                    error = $"节点执行器不可用：{node.TypeId}@{node.TypeVersion}。";
                }
                else
                {
                    var inputs = BuildInputs(flow, node, definition, results);
                    var runtimeInputs = BuildRuntimeInputs(flow, node, definition, results, runtimeOutputs);
                    var frames = new Dictionary<string, FrameReference>();
                    foreach (var edge in flow.Edges.Where(edge => edge.ToNodeId == node.Id))
                        if (spatialOutputs.TryGetValue(edge.FromNodeId, out var sourceFrames) && sourceFrames.TryGetValue(edge.FromPortId, out var frame))
                            frames[edge.ToPortId] = frame;
                    var signature = WorkflowConfiguration.NodeSignature(configurationFlow ?? flow, node.Id);
                    InspectionPlan.RequireFrame(flow, node.Id, frames, spatialOutputs, id);
                    var context = new NodeExecutionContext(node, inputs, cancellationToken,
                        message => logs.Add($"{DateTime.UtcNow:O} [{node.DisplayName}] {message}"), runtimeInputs, id, frames, signature);
                    var output = await executor.ExecuteAsync(context).ConfigureAwait(false);
                    if (output.RuntimePorts is { Count: > 0 }) runtimeOutputs[node.Id] = new Dictionary<string, object?>(output.RuntimePorts);
                    outputs = output.Ports.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                    spatialOutputs[node.Id] = DescribeFrames(node, definition, outputs, frames, id, signature);
                    status = NodeExecutionStatus.Success;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                status = NodeExecutionStatus.Skipped;
                error = "运行已取消。";
            }
            catch (Exception ex)
            {
                status = NodeExecutionStatus.Failed;
                error = ex.ToString();
                errors.Add($"节点“{node.DisplayName}”执行失败：{ex.Message}");
            }

            timer.Stop();
            var completed = new NodeExecutionResult(node.Id, node.DisplayName, status, nodeStarted, DateTime.UtcNow, timer.Elapsed.TotalMilliseconds, outputs, error);
            results[node.Id] = completed;
            progress?.Report(completed);
        }

        var runStatus = cancellationToken.IsCancellationRequested
            ? WorkflowRunStatus.Cancelled
            : errors.Count > 0 ? WorkflowRunStatus.Failed : WorkflowRunStatus.Success;
        var completedRun = new WorkflowExecutionRun(id, flow.Id, startedAt, DateTime.UtcNow, runStatus,
            order.Where(node => results.ContainsKey(node.Id)).Select(node => results[node.Id]).ToArray(), errors, logs,
            runtimeOutputs.ToDictionary(pair => pair.Key, pair => pair.Value), spatialOutputs,
            Inspect(runStatus, order.Where(node => results.ContainsKey(node.Id)).Select(node => results[node.Id]).ToArray()));
        runtimeOwnershipTransferred = true;
        return completedRun;
        }
        finally
        {
            if (!runtimeOwnershipTransferred) DisposeRuntimeOutputs(runtimeOutputs);
        }
    }

    private static IReadOnlyDictionary<string, FrameReference> DescribeFrames(NodeInstance node, NodeDefinition definition,
        IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyDictionary<string, FrameReference> inputs, Guid runId, string signature)
    {
        var result = new Dictionary<string, FrameReference>();
        var source = inputs.Values.DistinctBy(frame => frame.SpaceId).ToArray();
        foreach (var port in definition.Ports.Where(p => p.Direction == PortDirection.Output && p.DataType is "halcon.image" or "halcon.region" or "any"))
        {
            if (!outputs.TryGetValue(port.Id, out var value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width) ||
                !value.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height)) continue;
            var count = value.TryGetProperty("count", out var n) && n.TryGetInt32(out var objects) ? objects : source.FirstOrDefault()?.Count ?? 1;
            var parent = source.Length == 1 ? source[0] : null;
            if (parent is null)
                result[port.Id] = new(runId, Guid.NewGuid(), node.Id, port.Id, Guid.NewGuid(), width, height, count, port.Annotated, false, signature);
            else
                result[port.Id] = parent with { SpaceId = definition.ImageSpace == ImageSpaceBehavior.NewSpace ? node.Id : parent.SpaceId,
                    Width = width, Height = height, Count = count, Annotated = parent.Annotated || port.Annotated,
                    Corrected = parent.Corrected || definition.ImageSpace == ImageSpaceBehavior.NewSpace, ConfigurationIdentity = signature };
        }
        // Typed geometry keeps frame identity. Ambiguous captures/spaces have no
        // inferred provenance and must be rejected by downstream measurement.
        var geometrySources = inputs.Values.DistinctBy(f => (f.RunId, f.FrameId, f.SourceNodeId, f.SourcePortId, f.SpaceId, f.Width, f.Height, f.Count, f.ImageIndex, f.Annotated, f.Corrected)).ToArray();
        if (geometrySources is [var geometrySource] && geometrySource.RunId == runId && geometrySource.Count == 1)
            foreach (var port in definition.Ports.Where(p => p.Direction == PortDirection.Output && p.DataType is "halcon.circle2d" or "halcon.line2d" or "halcon.measurement2d"))
                if (outputs.TryGetValue(port.Id, out var geometry) && geometry.ValueKind == JsonValueKind.Object)
                    result[port.Id] = geometrySource with { ConfigurationIdentity = signature };
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildInputs(
        FlowDocument flow,
        NodeInstance node,
        NodeDefinition definition,
        IReadOnlyDictionary<Guid, NodeExecutionResult> results)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (var port in definition.Ports.Where(port => port.Direction == PortDirection.Input))
        {
            var links = flow.Edges.Where(edge => edge.ToNodeId == node.Id && edge.ToPortId == port.Id).ToArray();
            if (links.Length == 0) continue;
            var connectedValues = links
                .Select(edge => results[edge.FromNodeId].Outputs.TryGetValue(edge.FromPortId, out var value)
                    ? value.Clone()
                    : throw new InvalidDataException($"上游节点没有输出端口“{edge.FromPortId}”的值。"))
                .ToArray();
            values[port.Id] = port.Multiple ? JsonSerializer.SerializeToElement(connectedValues) : connectedValues[0];
        }
        return values;
    }

    private static IReadOnlyDictionary<string, object?> BuildRuntimeInputs(
        FlowDocument flow,
        NodeInstance node,
        NodeDefinition definition,
        IReadOnlyDictionary<Guid, NodeExecutionResult> results,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        var values = new Dictionary<string, object?>();
        foreach (var port in definition.Ports.Where(port => port.Direction == PortDirection.Input))
        {
            var links = flow.Edges.Where(edge => edge.ToNodeId == node.Id && edge.ToPortId == port.Id).ToArray();
            if (links.Length == 0) continue;
            var connectedValues = links.Select(edge =>
            {
                if (runtimeOutputs.TryGetValue(edge.FromNodeId, out var outputs) && outputs.TryGetValue(edge.FromPortId, out var runtimeValue))
                    return runtimeValue;
                return results[edge.FromNodeId].Outputs.TryGetValue(edge.FromPortId, out var serializedValue)
                    ? (object?)serializedValue.Clone()
                    : throw new InvalidDataException($"上游节点没有输出端口“{edge.FromPortId}”的运行值。");
            }).ToArray();
            values[port.Id] = port.Multiple ? connectedValues : connectedValues[0];
        }
        return values;
    }

    private static void DisposeRuntimeOutputs(IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var value in runtimeOutputs.Values.SelectMany(outputs => outputs.Values))
        {
            if (value is IDisposable disposable && disposed.Add(value))
            {
                try { disposable.Dispose(); } catch { /* A disposal failure must not hide the node result. */ }
            }
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyOutputs() => new Dictionary<string, JsonElement>();
}
