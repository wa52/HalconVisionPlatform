namespace HalconVision.Domain;

/// <summary>HALCON model-origin pose in original image coordinates; angles are degrees.</summary>
public sealed record QuickMatchPose(double Row, double Column, double AngleDegrees, double Score);
public sealed record QuickMatchImageResult(int ImageIndex, IReadOnlyList<QuickMatchPose> Matches,
    IReadOnlyList<QuickMatchPose>? RawCandidates = null, QuickMatchStatus? Status = null, string? RejectReason = null,
    double SearchMilliseconds = 0, double EvaluationMilliseconds = 0)
{
    public IReadOnlyList<QuickMatchPose> AcceptedMatches => Matches;
}
public sealed record QuickMatchTiming(double ModelMilliseconds, double SearchMilliseconds, double EvaluationMilliseconds, double TotalMilliseconds);
public sealed record QuickMatchGraphic(QuickMatchPose Pose, IReadOnlyList<IReadOnlyList<Point2D>> Contours);
public sealed record QuickMatchFrameOverlay(int ImageIndex, Rectangle2D? SearchRegion, IReadOnlyList<QuickMatchGraphic> Matches);
public sealed record QuickMatchOverlay(IReadOnlyList<QuickMatchFrameOverlay> Images);
public sealed record QuickMatchStatus(string Code, string Message)
{
    public const string Found = "MATCH_OK";
    public const string NotFound = "NOT_FOUND";
    public const string Partial = "MATCH_PARTIAL";
    public const string Ambiguous = "AMBIGUOUS";
    public const string OutOfRange = "OUT_OF_RANGE";
    public const string Timeout = "TIMEOUT";
    public const string ModelInvalid = "MODEL_INVALID";
    public const string Rejected = "MATCH_REJECTED";
    public static bool IsKnown(string? code) => code is Found or NotFound or Partial or Ambiguous or OutOfRange or Timeout or ModelInvalid or Rejected or "MATCH_FOUND" or "MATCH_NOT_FOUND";
}
