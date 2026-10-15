namespace HalconVision.Domain;

public sealed record ClassificationScore(int ClassId, string ClassName, double Confidence);
public sealed record ClassificationStatus(string Code, string Message)
{
    public const string Classified = "CLASSIFIED";
    public const string LowConfidence = "LOW_CONFIDENCE";
}
public sealed record ImageRoiOverlay(Rectangle2D Bounds);
