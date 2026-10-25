namespace HalconVision.Domain;

/// <summary>Distances to the infinite line defined by two image points; all units are pixels.</summary>
public sealed record LineCircleMeasurement(Line2D Line, Circle2D Circle, Point2D Projection,
    bool ProjectionWithinSegment, double CenterToLine, double BoundaryToLine, double SignedClearance,
    string Relation, Line2D CenterNormal, Line2D? BoundaryLine);
public sealed record LineCircleOverlay(LineCircleMeasurement? Measurement, bool ShowNormal, bool ShowBoundaryLine);
public sealed record LineCircleStatus(string Code, string Message)
{
    public const string Measured = "LINE_CIRCLE_MEASURED";
    public const string Missing = "LINE_CIRCLE_MISSING";
}
