namespace HalconVision.Domain;

public sealed record SegmentationObject(int Index, int ClassId, string ClassName, double Area,
    double AreaRatio, Point2D Center, Rectangle2D Bounds, Guid? RoiId = null, string? RoiName = null);
public sealed record SegmentationClassArea(int ClassId, string ClassName, double Pixels, double Ratio);
public sealed record SegmentationStatus(string Code, string Message)
{
    public const string Detected = "DETECTED";
    public const string Empty = "EMPTY";
}
