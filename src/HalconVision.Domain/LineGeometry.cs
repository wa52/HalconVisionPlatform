namespace HalconVision.Domain;

/// <summary>A two-dimensional line segment expressed in image row/column coordinates.</summary>
public sealed record Line2D(double RowBegin, double ColumnBegin, double RowEnd, double ColumnEnd)
{
    public double Length => Math.Sqrt(Math.Pow(RowEnd - RowBegin, 2) + Math.Pow(ColumnEnd - ColumnBegin, 2));
    public double AngleDegrees => Math.Atan2(-(RowEnd - RowBegin), ColumnEnd - ColumnBegin) * 180d / Math.PI;
}

public sealed record Point2D(double Row, double Column);

public sealed record PointSet(IReadOnlyList<Point2D> Points);
public sealed record Rectangle2D(double RowBegin, double ColumnBegin, double RowEnd, double ColumnEnd);

public sealed record GeometryOverlay(
    Line2D ReferenceLine,
    Line2D? FittedLine,
    IReadOnlyList<IReadOnlyList<Point2D>> MeasureRegions,
    PointSet EdgePoints,
    Rectangle2D? SearchRegion = null);

public sealed record LineFindStatus(string Code, string Message)
{
    public const string Found = "FOUND";
    public const string NotFound = "NOT_FOUND";
}
