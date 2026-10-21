using System.Globalization;
using HalconVision.Domain;

namespace HalconVision.Halcon;

internal static class TaskRoiScope
{
    public static Rectangle2D? Read(NodeInstance node, int width, int height)
    {
        if (!node.Parameters.TryGetValue("roi", out var value) || string.IsNullOrWhiteSpace(value.GetString())) return null;
        var parts = value.GetString()!.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length != 4 || !parts.All(double.IsFinite)) throw new InvalidDataException("ROI 坐标无效。");
        var b = new Rectangle2D(Math.Min(parts[0], parts[2]), Math.Min(parts[1], parts[3]), Math.Max(parts[0], parts[2]), Math.Max(parts[1], parts[3]));
        if (b.RowBegin < 0 || b.ColumnBegin < 0 || b.RowEnd >= height || b.ColumnEnd >= width || b.RowEnd <= b.RowBegin || b.ColumnEnd <= b.ColumnBegin)
            throw new InvalidDataException("ROI 超出图像或面积为空。");
        return b;
    }
}
