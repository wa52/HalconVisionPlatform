namespace HalconVision.Domain;

/// <summary>HALCON model-origin pose in original image coordinates; angles are degrees.</summary>
public sealed record QuickMatchPose(double Row, double Column, double AngleDegrees, double Score);
public sealed record QuickMatchImageResult(int ImageIndex, IReadOnlyList<QuickMatchPose> Matches);
public sealed record QuickMatchGraphic(QuickMatchPose Pose, IReadOnlyList<IReadOnlyList<Point2D>> Contours);
public sealed record QuickMatchFrameOverlay(int ImageIndex, Rectangle2D? SearchRegion, IReadOnlyList<QuickMatchGraphic> Matches);
public sealed record QuickMatchOverlay(IReadOnlyList<QuickMatchFrameOverlay> Images);
public sealed record QuickMatchStatus(string Code, string Message)
{
    public const string Found = "MATCH_FOUND";
    public const string NotFound = "MATCH_NOT_FOUND";
    public const string Partial = "MATCH_PARTIAL";
}
