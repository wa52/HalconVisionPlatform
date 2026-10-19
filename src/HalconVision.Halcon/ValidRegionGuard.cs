using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

internal static class ValidRegionGuard
{
    public static void Require(NodeExecutionContext context, HImage image, IEnumerable<Rectangle2D> rectangles)
    {
        if (context.InputFrames?.GetValueOrDefault("image")?.Corrected != true) return;
        image.GetImageSize(out int width, out int height);
        if (context.Node.Parameters.TryGetValue("rois", out var list))
        {
            foreach (var roi in DetectionRoi.Parse(list).Where(r => r.Enabled)) CheckBounds(roi.RowBegin, roi.ColumnBegin, roi.RowEnd, roi.ColumnEnd);
        }
        else if (context.Node.Parameters.TryGetValue("roi", out var raw) && raw.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(raw.GetString()))
        {
            var values = raw.GetString()!.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (values.Length == 4) CheckBounds(values[0], values[1], values[2], values[3]);
        }
        using var domain = image.GetDomain();
        foreach (var box in rectangles)
        {
            using var requested = new HRegion(box.RowBegin, box.ColumnBegin, box.RowEnd, box.ColumnEnd);
            using var missing = requested.Difference(domain);
            using var area = missing.AreaCenter(out HTuple rows, out HTuple columns); using var rowsScope = rows; using var columnsScope = columns;
            using var realArea = area.TupleReal();
            if (realArea.DArr.Sum() > 0) throw new InvalidDataException("ROI_OUTSIDE_VALID_IMAGE：检测范围包含矫正后缺失的像素，请把 ROI 放在有效区域内。");
        }
        void CheckBounds(double r1, double c1, double r2, double c2)
        {
            if (Math.Min(r1, r2) < 0 || Math.Min(c1, c2) < 0 || Math.Max(r1, r2) > height - 1 || Math.Max(c1, c2) > width - 1)
                throw new InvalidDataException("ROI_OUTSIDE_VALID_IMAGE：检测范围超出矫正图尺寸，不能自动裁小 ROI。");
        }
    }
}
