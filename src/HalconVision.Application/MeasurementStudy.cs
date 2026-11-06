using System.Globalization;
using System.Text;
using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public sealed record MeasurementStudySettings(Guid FlowId, Guid NodeId, string NodeName,
    string PortId, string PortName, string Unit, string ConfigurationIdentity,
    string ReferenceName, double ReferenceValue);
public sealed record MeasurementObservation(int Index, Guid RunId, DateTime CapturedAtUtc,
    string SampleName, double? Value, string State, string Reason, FrameReference? Frame, string ImageSource);
public sealed record MeasurementStatistics(int Total, int Valid, int Missing,
    double? Mean, double? Bias, double? SampleStandardDeviation, double? Minimum,
    double? Maximum, double? Range, double? MaximumAbsoluteError);
public sealed record MeasurementStudyReport(int SchemaVersion, DateTime ExportedAtUtc,
    MeasurementStudySettings Settings, MeasurementStatistics Statistics,
    IReadOnlyList<MeasurementObservation> Samples, JsonElement Configuration, string Limitation);

/// <summary>Managed snapshots only. Never retains a run or native image/model.</summary>
public sealed class MeasurementStudy
{
    public const int MaximumSamples = 10000;
    public const string Limitation = "统计仅描述已记录样本；同图重复计算不代表真实重复取像。未评估参考量具不确定度、全视场、跨日期/人员及 MSA，不自动判定标定合格。";
    private readonly List<MeasurementObservation> _samples = [];
    private readonly HashSet<Guid> _runs = [];
    private JsonElement _configuration;
    public MeasurementStudySettings Settings { get; }
    public IReadOnlyList<MeasurementObservation> Samples => _samples.AsReadOnly();

    public MeasurementStudy(MeasurementStudySettings settings)
    {
        if (settings.FlowId == Guid.Empty || settings.NodeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(settings.PortId) || settings.Unit is not ("px" or "mm") ||
            string.IsNullOrWhiteSpace(settings.ConfigurationIdentity) || settings.ConfigurationIdentity == "INVALID" ||
            string.IsNullOrWhiteSpace(settings.ReferenceName) || !double.IsFinite(settings.ReferenceValue))
            throw new ArgumentException("请选择像素/毫米长度字段，填写标准件名称和有限参考值。");
        Settings = settings;
    }

    public static IReadOnlyList<(NodeInstance Node, PortDefinition Port)> AvailableFields(
        FlowDocument flow, IReadOnlyCollection<NodeDefinition> definitions) => flow.Nodes
        .SelectMany(node => definitions.FirstOrDefault(d => d.TypeId == node.TypeId && d.Version == node.TypeVersion)?.Ports
            .Where(p => p.Direction == PortDirection.Output && p.DataType == "number" && p.Unit is "px" or "mm")
            .Select(port => (node, port)) ?? []).ToArray();

    public MeasurementObservation Capture(FlowDocument currentFlow, WorkflowExecutionRun run, string sampleName)
    {
        if (_samples.Count >= MaximumSamples) throw new InvalidOperationException("已达到 10000 条上限，请导出并新建验证记录。");
        if (string.IsNullOrWhiteSpace(sampleName)) throw new InvalidOperationException("请填写采样位置/样本编号。");
        if (run.Id == Guid.Empty || run.FlowId != Settings.FlowId || currentFlow.Id != Settings.FlowId)
            throw new InvalidOperationException("当前结果不属于所选流程，请重新运行。");
        if (_runs.Contains(run.Id)) throw new InvalidOperationException("这一轮已经记录，请重新取像运行后再记录。");
        var identity = InspectionPlan.ConfigurationIdentity(currentFlow);
        if (identity != Settings.ConfigurationIdentity || run.Inspection?.ConfigurationIdentity != identity)
            throw new InvalidOperationException("流程或标定参数已改变，或结果过期。请导出旧记录并新建验证。");
        var result = run.NodeResults.FirstOrDefault(n => n.NodeId == Settings.NodeId);
        var frame = run.SpatialOutputs?.GetValueOrDefault(Settings.NodeId)?.GetValueOrDefault(Settings.PortId);
        var reason = "";
        double? value = null;
        if (run.Status != WorkflowRunStatus.Success) reason = "流程未成功完成：" + run.Status;
        else if (result?.Status != NodeExecutionStatus.Success) reason = "测量节点未成功完成";
        else if (!result.Outputs.TryGetValue(Settings.PortId, out var field) || field.ValueKind != JsonValueKind.Number ||
                 !field.TryGetDouble(out var number) || !double.IsFinite(number)) reason = "测量缺失或不是有限数值";
        else if (frame is null || frame.RunId != run.Id || frame.Count != 1 || frame.Annotated ||
                 frame.Width <= 0 || frame.Height <= 0 || frame.ConfigurationIdentity != WorkflowConfiguration.NodeSignature(currentFlow, Settings.NodeId)) reason = "测量缺少本轮有效单帧来源";
        else value = number;
        if (value.HasValue && _samples.FirstOrDefault(s => s.Value.HasValue)?.Frame is { } previous && frame is not null &&
            (previous.Width != frame.Width || previous.Height != frame.Height || previous.SourceNodeId != frame.SourceNodeId ||
             previous.SourcePortId != frame.SourcePortId || previous.Corrected != frame.Corrected || previous.ImageIndex != frame.ImageIndex))
            throw new InvalidOperationException("图像来源、尺寸或空间模式已改变，请新建验证，避免混入不同条件。");
        if (value is { } measured && !double.IsFinite(measured - Settings.ReferenceValue))
            throw new InvalidOperationException("误差计算超出数值范围，未记录该样本。");
        var sample = new MeasurementObservation(_samples.Count + 1, run.Id, run.EndedAtUtc.ToUniversalTime(),
            sampleName.Trim(), value, value.HasValue ? "有效" : "失测", reason, frame, ImageSource(run, frame));
        // Compute before committing: a rejected overflow must not poison subsequent export.
        Calculate(_samples.Append(sample), Settings.ReferenceValue);
        if (_samples.Count == 0) _configuration = JsonSerializer.SerializeToElement(currentFlow);
        _samples.Add(sample); _runs.Add(run.Id);
        return sample;
    }

    public MeasurementStatistics Statistics => Calculate(_samples, Settings.ReferenceValue);
    private static MeasurementStatistics Calculate(IEnumerable<MeasurementObservation> observations, double reference)
    {
        var samples = observations.ToArray(); var values = samples.Where(s => s.Value.HasValue).Select(s => s.Value!.Value).ToArray();
        if (values.Length == 0) return new(samples.Length, 0, samples.Length, null, null, null, null, null, null, null);
        // Shift by the first observation to retain precision for small spreads on large baselines.
        double meanDelta = 0, m2 = 0; int count = 0; var origin = values[0];
        foreach (var value in values)
        {
            var deltaValue = value - origin; count++;
            var delta = deltaValue - meanDelta; meanDelta += delta / count;
            m2 += delta * (deltaValue - meanDelta);
        }
        var mean = origin + meanDelta; var min = values.Min(); var max = values.Max();
        double? deviation = count < 2 ? null : Math.Sqrt(Math.Max(0, m2) / (count - 1));
        var bias = mean - reference; var range = max - min; var absolute = values.Max(v => Math.Abs(v - reference));
        if (!new[] { mean, bias, range, absolute, m2 }.All(double.IsFinite) || deviation is { } sd && !double.IsFinite(sd))
            throw new InvalidOperationException("统计计算超出数值范围，未记录该样本。");
        return new(samples.Length, count, samples.Length - count, mean, bias, deviation, min, max, range, absolute);
    }

    private static string ImageSource(WorkflowExecutionRun run, FrameReference? frame)
    {
        var source = run.NodeResults.FirstOrDefault(n => n.NodeId == frame?.SourceNodeId);
        if (source?.Outputs.TryGetValue(frame!.SourcePortId, out var image) != true || image.ValueKind != JsonValueKind.Object) return "来源未提供文件信息";
        if (image.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array && frame.ImageIndex > 0 && paths.GetArrayLength() >= frame.ImageIndex)
            return paths[frame.ImageIndex - 1].GetString() ?? "";
        return image.TryGetProperty("source", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "来源未提供文件信息";
    }
    public MeasurementStudyReport Report() => new(1, DateTime.UtcNow, Settings, Statistics, _samples.ToArray(),
        _configuration.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<object?>(null) : _configuration, Limitation);
    public string ToJson() => JsonSerializer.Serialize(Report(), new JsonSerializerOptions { WriteIndented = true });
    public string ToCsv()
    {
        static string Cell(string text)
        {
            // Text identifiers cannot become spreadsheet formulas when opened in Excel.
            if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        static string N(double? v) => v?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        var s = Statistics; var b = new StringBuilder();
        b.AppendLine("项目,值");
        foreach (var entry in new[] { ("标准件", Settings.ReferenceName), ("节点", Settings.NodeName), ("字段", Settings.PortId),
            ("单位", Settings.Unit), ("参考值", N(Settings.ReferenceValue)), ("配置身份", Settings.ConfigurationIdentity),
            ("总数", s.Total.ToString(CultureInfo.InvariantCulture)), ("有效数", s.Valid.ToString(CultureInfo.InvariantCulture)),
            ("失测数", s.Missing.ToString(CultureInfo.InvariantCulture)), ("均值", N(s.Mean)), ("偏差", N(s.Bias)),
            ("样本标准差(n-1)", N(s.SampleStandardDeviation)), ("最小值", N(s.Minimum)), ("最大值", N(s.Maximum)),
            ("极差", N(s.Range)), ("最大绝对误差", N(s.MaximumAbsoluteError)), ("范围声明", Limitation) })
            b.AppendLine(Cell(entry.Item1) + "," + Cell(entry.Item2));
        b.AppendLine(); b.AppendLine("序号,运行ID,时间UTC,样本/位置,测量值,误差,状态,原因,帧ID,源节点ID,空间ID,宽,高,图像来源");
        foreach (var o in _samples)
            b.AppendLine(string.Join(",", o.Index.ToString(CultureInfo.InvariantCulture), o.RunId,
                Cell(o.CapturedAtUtc.ToString("O")), Cell(o.SampleName), N(o.Value), N(o.Value - Settings.ReferenceValue),
                Cell(o.State), Cell(o.Reason), o.Frame?.FrameId, o.Frame?.SourceNodeId, o.Frame?.SpaceId, o.Frame?.Width, o.Frame?.Height, Cell(o.ImageSource)));
        return b.ToString();
    }
}
