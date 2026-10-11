namespace HalconVision.Domain;

public sealed record InspectionResult(
    int ComponentCount,
    int ExpectedComponentCount,
    bool IsPass,
    double ElapsedMilliseconds,
    IReadOnlyList<DetectedComponent> Components);

public sealed record DetectedComponent(int X, int Y, int Width, int Height);
