namespace HalconVision.Domain;

/// <summary>A measured circle in the input image's row/column coordinate system; lengths are pixels.</summary>
public sealed record Circle2D(double Row, double Column, double Radius)
{
    public double Diameter => 2 * Radius;
    public double Circumference => 2 * Math.PI * Radius;

    public IReadOnlyList<Point2D> Outline() => Enumerable.Range(0, 361)
        .Select(i => new Point2D(Row + Radius * Math.Sin(i * Math.PI / 180), Column + Radius * Math.Cos(i * Math.PI / 180))).ToArray();
}

public sealed record CircleGeometryOverlay(Rectangle2D SearchRegion, Circle2D? FittedCircle,
    PointSet EdgePoints, IReadOnlyList<IReadOnlyList<Point2D>> MeasureRegions,
    bool ShowSearchRegion = true, bool ShowFittedCircle = true, bool ShowDiagnostics = false);

public sealed record CircleFindStatus(string Code, string Message)
{
    public const string Found = "CIRCLE_FOUND";
    public const string NotFound = "CIRCLE_NOT_FOUND";
}
