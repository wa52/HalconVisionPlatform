using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Application;

public static class ProductInspectionEvaluator
{
    public static ProductInspectionResult Evaluate(FlowDocument flow, Guid runId, WorkflowRunStatus status,
        IReadOnlyList<NodeExecutionResult> nodes, IReadOnlyList<string> errors)
    {
        try { return EvaluateCore(flow, runId, status, nodes, errors) with { ConfigurationIdentity = InspectionPlan.ConfigurationIdentity(flow) }; }
        catch (Exception error) { return new(runId, flow.Id, InspectionVerdict.Error, [], ["判定配置或结果无效：" + error.Message]); }
    }

    private static ProductInspectionResult EvaluateCore(FlowDocument flow, Guid runId, WorkflowRunStatus status,
        IReadOnlyList<NodeExecutionResult> nodes, IReadOnlyList<string> errors)
    {
        if (flow.InspectionTasks.Count == 0) return new(runId, flow.Id, InspectionVerdict.NotConfigured, [], ["尚未配置检测任务，执行成功不能作为产品放行结论。"]);
        var results = new List<InspectionTaskResult>();
        foreach (var task in flow.InspectionTasks)
        {
            var reasons = new List<string>(); var verdict = InspectionVerdict.Ok;
            if (!task.Enabled)
            {
                results.Add(new(task.Id, task.Name, task.Required, task.Required ? InspectionVerdict.Error : InspectionVerdict.Skipped, [task.Required ? "必检任务已禁用。" : "可选任务已禁用。"])); continue;
            }
            if (task.NodeIds.Count == 0 || task.Rules.Count == 0) { verdict = InspectionVerdict.Error; reasons.Add("任务缺少算法节点或判定规则。"); }
            foreach (var id in task.NodeIds)
            {
                var matches = nodes.Where(n => n.NodeId == id).ToArray();
                if (matches.Length != 1 || matches[0].Status != NodeExecutionStatus.Success)
                { verdict = InspectionVerdict.Error; reasons.Add($"节点 {id} 未完成可信执行。"); }
                else if (matches[0].Outputs.TryGetValue("status", out var quality) && quality.ValueKind == JsonValueKind.Object &&
                    quality.TryGetProperty("Code", out var qualityCode) && QuickMatchStatus.IsKnown(qualityCode.GetString()) &&
                    qualityCode.GetString() is not ("MATCH_OK" or "MATCH_FOUND" or "NOT_FOUND" or "MATCH_NOT_FOUND"))
                { verdict = InspectionVerdict.Error; reasons.Add($"{matches[0].NodeName}：匹配质量不可信（{qualityCode.GetString()}）。"); }
            }
            foreach (var rule in task.Rules)
            {
                try
                {
                    if (!task.NodeIds.Contains(rule.NodeId)) throw new InvalidDataException("规则引用任务外节点。");
                    var node = nodes.Single(n => n.NodeId == rule.NodeId);
                    if (node.Status != NodeExecutionStatus.Success) throw new InvalidDataException("规则来源执行未成功。");
                    if (!node.Outputs.TryGetValue(rule.PortId, out var value)) throw new InvalidDataException("判定输出缺失。");
                    value = Select(value, rule.Pointer);
                    bool accepted;
                    switch (rule.Kind)
                    {
                        case InspectionRuleKind.NumberRange:
                            if (!double.IsFinite(rule.Minimum) || !double.IsFinite(rule.Maximum) || rule.Minimum > rule.Maximum ||
                                value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                                throw new InvalidDataException("数值或上下限无效。");
                            accepted = number >= rule.Minimum && number <= rule.Maximum; break;
                        case InspectionRuleKind.TextEquals:
                            if (string.IsNullOrWhiteSpace(rule.Expected) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException("期望文本或结果无效。");
                            accepted = value.GetString() == rule.Expected; break;
                        case InspectionRuleKind.BooleanTrue:
                            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("结果不是布尔值。");
                            accepted = value.GetBoolean(); break;
                        case InspectionRuleKind.DetectionDecision:
                            if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("OK" or "NOT_DETECTED" or "DETECTED_NG")) throw new InvalidDataException("检测判定缺失或未知。");
                            accepted = value.GetString() == "OK"; break;
                        case InspectionRuleKind.MatchStatus:
                            if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("MATCH_OK" or "NOT_FOUND")) throw new InvalidDataException("定位结果不可信。");
                            accepted = value.GetString() == "MATCH_OK"; break;
                        default: throw new InvalidDataException("未知判定方式。");
                    }
                    if (!accepted) { if (verdict != InspectionVerdict.Error) verdict = InspectionVerdict.Ng; reasons.Add($"{node.NodeName} / {rule.PortId}{rule.Pointer} 未满足规则。"); }
                }
                catch (Exception error) { verdict = InspectionVerdict.Error; reasons.Add($"规则 {rule.Id}：{error.Message}"); }
            }
            results.Add(new(task.Id, task.Name, task.Required, verdict, reasons.Count == 0 ? ["全部规则满足。"] : reasons));
        }
        var global = errors.ToList();
        if (status != WorkflowRunStatus.Success) global.Add("流程失败或已取消。");
        if (!flow.InspectionTasks.Any(t => t.Required && t.Enabled)) global.Add("没有已启用的必检任务。");
        var outcome = global.Count > 0 || results.Any(r => r.Verdict == InspectionVerdict.Error) ? InspectionVerdict.Error :
            results.Any(r => r.Required && r.Verdict == InspectionVerdict.Ng) ? InspectionVerdict.Ng : InspectionVerdict.Ok;
        return new(runId, flow.Id, outcome, results, global);
    }

    private static JsonElement Select(JsonElement value, string pointer)
    {
        if (pointer.Length == 0) return value;
        if (!pointer.StartsWith('/')) throw new InvalidDataException("字段使用 JSON Pointer，例如 /Code。");
        foreach (var token in pointer[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(token, out var child)) value = child;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else throw new InvalidDataException("判定字段不存在。");
        }
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new InvalidDataException("判定字段为空。");
        return value;
    }
}
