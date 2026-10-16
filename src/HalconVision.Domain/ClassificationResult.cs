namespace HalconVision.Domain;

public sealed record ClassificationScore(int ClassId, string ClassName, double Confidence);
public sealed record ClassificationStatus(string Code, string Message)
{
    public const string Classified = "CLASSIFIED";
    public const string LowConfidence = "LOW_CONFIDENCE";
}
public sealed record ImageRoiOverlay(Rectangle2D Bounds);

/// <summary>Product judgement is separate from successful execution and raw model scores.</summary>
public sealed record ClassificationDecision(string Code, string Message)
{
    public const string Ok = "OK";
    public const string NotDetected = "NOT_DETECTED";
    public const string DetectedNg = "DETECTED_NG";

    public static ClassificationDecision From(string className, ClassificationStatus status, string ngPrefix) =>
        status.Code != ClassificationStatus.Classified || string.IsNullOrWhiteSpace(className)
            ? new(NotDetected, "NG · 未检出（置信度不足或分类无效）")
            : className.StartsWith(ngPrefix, StringComparison.OrdinalIgnoreCase)
                ? new(DetectedNg, "检出 NG") : new(Ok, "OK");
}
