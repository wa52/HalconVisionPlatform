using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed record NodeOutputValue(string Name, string DataType, string Value, string FullValue)
{
    public static IReadOnlyList<NodeOutputValue> From(NodeExecutionResult? result, NodeDefinition? definition)
    {
        if (result is null) return [new("运行状态", "状态", "未运行", "此节点尚无本次流程的输出数据。")];
        var rows = new List<NodeOutputValue>();
        rows.Add(new("检测结果", "状态", NodeResultSummary.Describe(result), result.Error ?? NodeResultSummary.Describe(result)));
        foreach (var (key, value) in result.Outputs)
        {
            var port = definition?.Ports.FirstOrDefault(item => item.Id == key && item.Direction == PortDirection.Output);
            Add(key, port?.DataType ?? value.ValueKind.ToString(), value);
        }
        return rows;

        void Add(string name, string type, JsonElement value)
        {
            var full = value.ValueKind == JsonValueKind.Undefined ? "" : value.GetRawText();
            var display = value.ValueKind switch
            {
                JsonValueKind.Null => "无结果",
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Object => "展开字段如下",
                JsonValueKind.Array => $"{value.GetArrayLength()} 项（选中查看完整数据）",
                _ => full
            };
            rows.Add(new(name, type, display, full));
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var field in value.EnumerateObject()) Add($"{name}.{field.Name}", field.Value.ValueKind.ToString(), field.Value);
            if (value.ValueKind == JsonValueKind.Array && name is "objects" or "classes" or "scores" or "roiResults")
            {
                var index = 0;
                foreach (var item in value.EnumerateArray().Take(200)) Add($"{name}[{index++}]", item.ValueKind.ToString(), item);
                if (value.GetArrayLength() > 200)
                    rows.Add(new($"{name}.显示说明", "说明", "仅展开前 200 项，选中数组行复制完整数据", full));
            }
        }
    }
}
