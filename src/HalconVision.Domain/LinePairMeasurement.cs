namespace HalconVision.Domain;

/// <summary>Undirected acute angle in degrees and minimum distance between finite image segments.</summary>
public sealed record LinePairMeasurement(Line2D LineA, Line2D LineB, double AngleDegrees,
    double SegmentDistance, Point2D ClosestA, Point2D ClosestB);
public sealed record LinePairOverlay(LinePairMeasurement? Measurement, bool ShowLines, bool ShowConnector);
public sealed record LinePairStatus(string Code, string Message)
{
    public const string Measured = "LINE_PAIR_MEASURED";
    public const string Missing = "LINE_PAIR_MISSING";
}
