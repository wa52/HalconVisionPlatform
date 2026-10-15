using System.Globalization;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Domain;

namespace HalconVision.Halcon;

internal sealed record DlCropRoi(int RowBegin, int ColumnBegin, int RowEnd, int ColumnEnd, bool IsExplicit)
{
    public int Width => ColumnEnd - ColumnBegin + 1;
    public int Height => RowEnd - RowBegin + 1;
    public Rectangle2D Bounds => new(RowBegin, ColumnBegin, RowEnd, ColumnEnd);
    public HImage Crop(HImage image) => image.CropRectangle1(RowBegin, ColumnBegin, RowEnd, ColumnEnd);
    public HRegion Region()
    {
        var region = new HRegion();
        try { region.GenRectangle1((double)RowBegin, ColumnBegin, RowEnd, ColumnEnd); return region; }
        catch { region.Dispose(); throw; }
    }
    public static DlCropRoi Read(NodeInstance node, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException("输入图像尺寸无效。");
        if (!node.Parameters.TryGetValue("roi", out var value) || value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
            return new(0, 0, height - 1, width - 1, false);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("ROI 必须为矩形坐标字符串。");
        var parts = value.GetString()!.Split(',');
        var coords = new double[4];
        if (parts.Length != 4) throw new InvalidDataException("ROI 应包含起终点行列坐标，请重新绘制矩形。");
        for (var index = 0; index < 4; index++)
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out coords[index]) || !double.IsFinite(coords[index]))
                throw new InvalidDataException("ROI 坐标必须为有限数值。");
        var top = Math.Min(coords[0], coords[2]); var bottom = Math.Max(coords[0], coords[2]);
        var left = Math.Min(coords[1], coords[3]); var right = Math.Max(coords[1], coords[3]);
        if (bottom < 0 || right < 0 || top > height - 1 || left > width - 1)
            throw new InvalidDataException("绘制的 ROI 完全位于图像外部，请重新绘制。");
        var roi = new DlCropRoi((int)Math.Floor(Math.Clamp(top, 0, height - 1)), (int)Math.Floor(Math.Clamp(left, 0, width - 1)),
            (int)Math.Ceiling(Math.Clamp(bottom, 0, height - 1)), (int)Math.Ceiling(Math.Clamp(right, 0, width - 1)), true);
        if (roi.Width < 2 || roi.Height < 2) throw new InvalidDataException("ROI 至少需要 2×2 个像素。");
        return roi;
    }
}
