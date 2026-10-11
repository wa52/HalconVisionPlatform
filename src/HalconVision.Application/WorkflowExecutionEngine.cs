using System.Diagnostics;
using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public sealed class WorkflowExecutionEngine(INodeRegistry registry, WorkflowGraphValidator validator)
{
    public async Task<WorkflowExecutionRun> RunAsync(
        FlowDocument flow,
        IProgress<NodeExecutionResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var id = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var results = new Dictionary<Guid, NodeExecutionResult>();
        var runtimeOutputs = new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();
        var logs = new List<string>();
        var errors = new List<string>();

        var issues = validator.Validate(flow);
        if (issues.Count > 0)
        {
            errors.AddRange(issues.Select(issue => issue.NodeId is { } nodeId
                ? $"节点 {nodeId}: {issue.Message}"
                : issue.Message));
            var failedAt = DateTime.UtcNow;
            return new WorkflowExecutionRun(id, flow.Id, startedAt, failedAt, WorkflowRunStatus.Failed, [], errors, logs);
        }

        IReadOnlyList<NodeInstance> order;
        try { order = validator.GetExecutionOrder(flow); }
        catch (WorkflowValidationException ex)
        {
            errors.AddRange(ex.Issues.Select(issue => issue.Message));
            return new WorkflowExecutionRun(id, flow.Id, startedAt, DateTime.UtcNow, WorkflowRunStatus.Failed, [], errors, logs);
        }

        var runtimeOwnershipTransferred = false;
        try
        {
        foreach (var node in order)
        {
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
                    var context = new NodeExecutionContext(node, inputs, cancellationToken,
                        message => logs.Add($"{DateTime.UtcNow:O} [{node.DisplayName}] {message}"), runtimeInputs);
                    var output = await executor.ExecuteAsync(context).ConfigureAwait(false);
                    if (output.RuntimePorts is { Count: > 0 }) runtimeOutputs[node.Id] = new Dictionary<string, object?>(output.RuntimePorts);
                    outputs = output.Ports.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
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
            runtimeOutputs.ToDictionary(pair => pair.Key, pair => pair.Value));
        runtimeOwnershipTransferred = true;
        return completedRun;
        }
        finally
        {
            if (!runtimeOwnershipTransferred) DisposeRuntimeOutputs(runtimeOutputs);
        }
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
