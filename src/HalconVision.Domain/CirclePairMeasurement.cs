namespace HalconVision.Domain;

/// <summary>Analytic circle distances in input-image pixels.</summary>
public sealed record CirclePairMeasurement(Circle2D CircleA, Circle2D CircleB,
    double CenterDistance, double BoundaryDistance, string Relation, Line2D CenterLine, Line2D? BoundaryLine);
public sealed record CirclePairOverlay(CirclePairMeasurement? Measurement, bool ShowCenterLine, bool ShowBoundaryLine);
public sealed record CirclePairStatus(string Code, string Message)
{
    public const string Measured = "CIRCLE_PAIR_MEASURED";
    public const string Missing = "CIRCLE_PAIR_MISSING";
}
