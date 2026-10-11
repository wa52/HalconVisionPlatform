using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public sealed record NodeParameterDefinition(string Id, string Name, string DataType, bool Required, JsonElement? DefaultValue = null);

public sealed record NodeDefinition(
    string TypeId,
    int Version,
    string Category,
    string DisplayName,
    string Description,
    IReadOnlyList<PortDefinition> Ports,
    IReadOnlyList<NodeParameterDefinition> Parameters);

public sealed record NodeExecutionContext(
    NodeInstance Node,
    IReadOnlyDictionary<string, JsonElement> Inputs,
    CancellationToken CancellationToken,
    Action<string> Log,
    IReadOnlyDictionary<string, object?>? RuntimeInputs = null);

public sealed record NodeExecutionOutput(
    IReadOnlyDictionary<string, JsonElement> Ports,
    IReadOnlyDictionary<string, object?>? RuntimePorts = null)
{
    public static NodeExecutionOutput Empty { get; } = new(new Dictionary<string, JsonElement>());
}

public sealed record WorkflowValidationIssue(string Message, Guid? NodeId = null, Guid? EdgeId = null);

public interface INodeDefinitionProvider
{
    IEnumerable<NodeDefinition> GetDefinitions();
}

public interface INodeExecutor
{
    string TypeId { get; }
    int Version { get; }
    ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context);
}

public interface INodeRegistry
{
    IReadOnlyCollection<NodeDefinition> Definitions { get; }
    void Register(INodeDefinitionProvider provider, IEnumerable<INodeExecutor> executors);
    bool TryGetDefinition(string typeId, int version, out NodeDefinition definition);
    bool TryGetExecutor(string typeId, int version, out INodeExecutor executor);
}

public sealed class NodeRegistry : INodeRegistry
{
    private readonly Dictionary<(string TypeId, int Version), NodeDefinition> _definitions = [];
    private readonly Dictionary<(string TypeId, int Version), INodeExecutor> _executors = [];

    public IReadOnlyCollection<NodeDefinition> Definitions => _definitions.Values.ToArray();

    public void Register(INodeDefinitionProvider provider, IEnumerable<INodeExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(executors);
        var definitions = provider.GetDefinitions().ToArray();
        var executorMap = executors.ToDictionary(item => (item.TypeId, item.Version));
        foreach (var definition in definitions)
        {
            var key = (definition.TypeId, definition.Version);
            if (_definitions.ContainsKey(key)) throw new InvalidOperationException($"节点类型已注册：{definition.TypeId}@{definition.Version}");
            if (!executorMap.ContainsKey(key)) throw new InvalidOperationException($"节点定义缺少对应执行器：{definition.TypeId}@{definition.Version}");
        }
        foreach (var definition in definitions)
        {
            var key = (definition.TypeId, definition.Version);
            _definitions.Add(key, definition);
            _executors.Add(key, executorMap[key]);
        }
    }

    public bool TryGetDefinition(string typeId, int version, out NodeDefinition definition) => _definitions.TryGetValue((typeId, version), out definition!);
    public bool TryGetExecutor(string typeId, int version, out INodeExecutor executor) => _executors.TryGetValue((typeId, version), out executor!);
}

public sealed class BuiltInTestNodeProvider : INodeDefinitionProvider
{
    public const string ConstantText = "test.constant-text";
    public const string PassThrough = "test.pass-through";
    public const string LogValue = "test.log-value";

    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new NodeDefinition(ConstantText, 1, "测试节点", "文本常量", "输出可配置的测试文本。", [new("text", "文本", PortDirection.Output, "string")], [new("value", "文本值", "string", true, JsonSerializer.SerializeToElement("Hello HALCON"))]);
        yield return new NodeDefinition(PassThrough, 1, "测试节点", "透传", "将输入值传递到输出端口。", [new("input", "输入", PortDirection.Input, "any", true), new("output", "输出", PortDirection.Output, "any")], []);
        yield return new NodeDefinition(LogValue, 1, "测试节点", "日志输出", "将输入值写入执行日志。", [new("value", "输入", PortDirection.Input, "any", true)], []);
    }
}

public sealed class ConstantTextExecutor : INodeExecutor
{
    public string TypeId => BuiltInTestNodeProvider.ConstantText;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var value = context.Node.Parameters.TryGetValue("value", out var configured) ? configured : JsonSerializer.SerializeToElement(string.Empty);
        context.Log($"输出文本：{value.GetString()}");
        return ValueTask.FromResult(new NodeExecutionOutput(new Dictionary<string, JsonElement> { ["text"] = value.Clone() }));
    }
}

public sealed class PassThroughExecutor : INodeExecutor
{
    public string TypeId => BuiltInTestNodeProvider.PassThrough;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!context.Inputs.TryGetValue("input", out var value)) throw new InvalidDataException("透传节点未收到输入值。");
        context.Log("已透传输入值。");
        var runtimePorts = context.RuntimeInputs is not null && context.RuntimeInputs.TryGetValue("input", out var runtimeValue) && runtimeValue is not JsonElement
            ? new Dictionary<string, object?> { ["output"] = runtimeValue }
            : null;
        return ValueTask.FromResult(new NodeExecutionOutput(new Dictionary<string, JsonElement> { ["output"] = value.Clone() }, runtimePorts));
    }
}

public sealed class LogValueExecutor : INodeExecutor
{
    public string TypeId => BuiltInTestNodeProvider.LogValue;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!context.Inputs.TryGetValue("value", out var value)) throw new InvalidDataException("日志节点未收到输入值。");
        context.Log($"执行值：{value.GetRawText()}");
        return ValueTask.FromResult(NodeExecutionOutput.Empty);
    }
}
