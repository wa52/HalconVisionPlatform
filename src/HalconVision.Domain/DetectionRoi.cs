using System.Text.Json;

namespace HalconVision.Domain;

public sealed record DetectionRoi(Guid Id, string Name, double RowBegin, double ColumnBegin, double RowEnd, double ColumnEnd, bool Enabled = true)
{
    public const int MaximumCount = 64;
    public static IReadOnlyList<DetectionRoi> Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ROI 列表必须为数组。");
        if (value.GetArrayLength() > MaximumCount) throw new InvalidDataException($"一个节点最多支持 {MaximumCount} 个 ROI。");
        var items = value.Deserialize<List<DetectionRoi>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (items.Any(item => item is null || item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) ||
            !double.IsFinite(item.RowBegin) || !double.IsFinite(item.ColumnBegin) || !double.IsFinite(item.RowEnd) || !double.IsFinite(item.ColumnEnd)))
            throw new InvalidDataException("ROI 的标识、名称或坐标无效。");
        if (items.Select(item => item.Id).Distinct().Count() != items.Count ||
            items.Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
            throw new InvalidDataException("ROI 标识和名称不能重复。");
        return items;
    }
}

public sealed record RoiDisplayResult(Guid Id, string Name, Rectangle2D Bounds, string Summary, bool Warning = false);
public sealed record MultiRoiOverlay(IReadOnlyList<RoiDisplayResult> Rois);
public sealed record RoiClassificationResult(Guid RoiId, string RoiName, Rectangle2D Bounds, int ClassId, string ClassName,
    double Confidence, IReadOnlyList<ClassificationScore> Scores, ClassificationStatus Status, double InferenceMilliseconds);
public sealed record RoiSegmentationResult(Guid RoiId, string RoiName, Rectangle2D Bounds, int Count, double Area,
    IReadOnlyList<SegmentationClassArea> Classes, SegmentationStatus Status, double InferenceMilliseconds);
