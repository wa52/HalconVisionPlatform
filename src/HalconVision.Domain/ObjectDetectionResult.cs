namespace HalconVision.Domain;

public sealed record DetectedObject(Guid RoiId, string RoiName, int ClassId, string ClassName, double Confidence, Rectangle2D Bounds);
public sealed record RoiObjectDetectionResult(Guid RoiId, string RoiName, Rectangle2D Bounds, string? ExpectedClass,
    IReadOnlyList<DetectedObject> Objects, IReadOnlyList<DetectedObject> Candidates,
    ClassificationDecision Decision, double InferenceMilliseconds);
