using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Media;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed class NodeResultSummary : IValueConverter
{
    public static string Describe(NodeExecutionResult? result)
    {
        if (result is null) return "未运行 · 请运行所选节点所在流程";
        if (result.Status != NodeExecutionStatus.Success)
            return result.Status == NodeExecutionStatus.Failed ? $"运行错误：{result.Error}" : $"未完成：{result.Status}";
        if (!result.Outputs.TryGetValue("status", out var status) || status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("Code", out var code)) return "执行成功";
        if (code.GetString() is ClassificationStatus.Classified or ClassificationStatus.LowConfidence)
            return (code.GetString() == ClassificationStatus.LowConfidence ? "分类置信度不足 · " : "分类完成 · ") +
                (status.TryGetProperty("Message", out var classificationMessage) ? classificationMessage.GetString() : "请查看分类数据");
        if (code.GetString() == SegmentationStatus.Detected)
            return "语义分割检出目标 · " + (status.TryGetProperty("Message", out var message) ? message.GetString() : "请查看区域数据");
        if (code.GetString() == SegmentationStatus.Empty)
            return "未检出分割目标 · 请检查模型、类别和面积条件";
        if (code.GetString() == LineFindStatus.NotFound)
            return "未找到直线 · 请调整矩形范围、方向或边缘阈值";
        if (code.GetString() != LineFindStatus.Found) return "检测状态未知";
        string Number(string key, string format) => result.Outputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number.ToString(format, CultureInfo.InvariantCulture) : "—";
        return $"找到直线 · 红色线为拟合结果 · 评分 {Number("score", "F3")} · 长度 {Number("length", "F1")} px · 角度 {Number("angle", "F2")}°";
    }

    public static Brush Color(NodeExecutionResult? result)
    {
        var text = Describe(result);
        return text.StartsWith("找到直线") || text.StartsWith("语义分割检出") || text.StartsWith("分类完成") ? Brushes.LightGreen
            : text.StartsWith("未找到") || text.StartsWith("未检出") || text.StartsWith("分类置信度不足") || text.StartsWith("运行错误") ? Brushes.Orange : Brushes.LightGray;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Describe(value as NodeExecutionResult);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
