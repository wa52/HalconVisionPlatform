using System.Globalization;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconCirclePairExecutor : INodeExecutor
{
    public string TypeId => HalconCirclePairNodeProvider.CirclePair;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var token = context.CancellationToken; token.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage image || !image.IsInitialized() || image.CountObj() != 1)
            throw new InvalidDataException("圆圆测量需要本轮的一张有效原始图像。");
        image.GetImageSize(out int width, out int height);
        var frame = context.InputFrames?.GetValueOrDefault("image");
        if (context.RunId == Guid.Empty || frame is null || frame.RunId != context.RunId || frame.FrameId == Guid.Empty || frame.SpaceId == Guid.Empty ||
            frame.Annotated || frame.Count != 1 || frame.ImageIndex != 1 || frame.Width != width || frame.Height != height)
            throw new InvalidDataException("MEASUREMENT_FRAME_REQUIRED：需要本轮未标注单帧及坐标来源，不能使用无来源或过期图像。");
        var a = ReadCircle("circleA"); var b = ReadCircle("circleB");
        CheckCircle(a, "circleA"); CheckCircle(b, "circleB");
        var showCenters = Flag("showCenterLine"); var showBoundary = Flag("showBoundaryLine");
        CirclePairMeasurement? measurement = null;
        var status = new CirclePairStatus(CirclePairStatus.Missing, "缺少有效圆，未生成测量值；请检查两个圆查找结果。");
        if (a is not null && b is not null)
        {
            var box = new Rectangle2D(Math.Min(a.Row - a.Radius, b.Row - b.Radius), Math.Min(a.Column - a.Radius, b.Column - b.Radius),
                Math.Max(a.Row + a.Radius, b.Row + b.Radius), Math.Max(a.Column + a.Radius, b.Column + b.Radius));
            if (context.Node.Parameters.TryGetValue("taskRoi", out var scoped))
            {
                var scopedNode = context.Node with { Parameters = new(context.Node.Parameters) { ["roi"] = scoped } };
                var bounds = TaskRoiScope.Read(scopedNode, width, height) ?? throw new InvalidDataException("测量任务区域为空。");
                if (box.RowBegin < bounds.RowBegin || box.ColumnBegin < bounds.ColumnBegin || box.RowEnd > bounds.RowEnd || box.ColumnEnd > bounds.ColumnEnd)
                    throw new InvalidDataException("MEASUREMENT_OUTSIDE_TASK：两个完整圆必须位于测量任务区域内。");
            }
            ValidRegionGuard.Require(context, image, [box]);
            using (var domain = image.GetDomain())
            using (var requested = new HRegion(Math.Floor(box.RowBegin), Math.Floor(box.ColumnBegin), Math.Ceiling(box.RowEnd), Math.Ceiling(box.ColumnEnd)))
            using (var missing = requested.Difference(domain))
            using (var area = missing.AreaCenter(out HTuple rows, out HTuple cols))
            using (rows) using (cols) using (var real = area.TupleReal())
                if (real.DArr.Sum() > 0) throw new InvalidDataException("MEASUREMENT_INVALID_PIXELS：两圆及连线的包围范围包含无效像素。");
            // Native HALCON distance_pp measures centers. Boundary distances use exact
            // circle geometry rather than sampling a polygonal approximation.
            var distance = HMisc.DistancePp(a.Row, a.Column, b.Row, b.Column);
            var gap = Math.Max(0, Math.Max(distance - a.Radius - b.Radius, Math.Abs(a.Radius - b.Radius) - distance));
            var epsilon = 1e-9 * Math.Max(1, Math.Max(distance, a.Radius + b.Radius));
            var relation = distance <= epsilon ? (Math.Abs(a.Radius - b.Radius) <= epsilon ? "重合" : "同心包含") :
                Math.Abs(distance - a.Radius - b.Radius) <= epsilon ? "外切" : distance > a.Radius + b.Radius ? "相离" :
                Math.Abs(distance - Math.Abs(a.Radius - b.Radius)) <= epsilon ? "内切" : distance < Math.Abs(a.Radius - b.Radius) ? "包含" : "相交";
            Line2D? boundary = null;
            if (gap > 0)
            {
                var dr = distance == 0 ? 0 : (b.Row - a.Row) / distance;
                var dc = distance == 0 ? 1 : (b.Column - a.Column) / distance;
                var signA = distance >= a.Radius + b.Radius || a.Radius >= b.Radius ? 1 : -1;
                var signB = distance >= a.Radius + b.Radius || b.Radius > a.Radius ? -1 : 1;
                boundary = new(a.Row + signA * dr * a.Radius, a.Column + signA * dc * a.Radius,
                    b.Row + signB * dr * b.Radius, b.Column + signB * dc * b.Radius);
            }
            measurement = new(a, b, distance, gap, relation, new(a.Row, a.Column, b.Row, b.Column), boundary);
            status = new(CirclePairStatus.Measured, $"测量完成 · 圆心距 {distance.ToString("F2", CultureInfo.InvariantCulture)} px · 圆周最短 {gap.ToString("F2", CultureInfo.InvariantCulture)} px · {relation}");
        }
        context.Log(status.Message); token.ThrowIfCancellationRequested();
        HImage? preview = null;
        try
        {
            using var owned = new OwnedPreviewImage(image, token); preview = owned.TakeImage();
            if (measurement is not null)
            {
                Paint(preview, a!.Outline(), [50, 205, 50]); Paint(preview, b!.Outline(), [50, 205, 50]);
                if (showCenters && measurement.CenterLine.Length > 1e-12) PaintLine(measurement.CenterLine, [0, 210, 255]);
                if (showBoundary && measurement.BoundaryLine is { Length: > 1e-12 } line) PaintLine(line, [255, 210, 0]);
            }
            token.ThrowIfCancellationRequested();
            var overlay = new CirclePairOverlay(measurement, showCenters, showBoundary);
            var ports = new Dictionary<string, JsonElement> { ["centerDistance"] = J(measurement?.CenterDistance), ["boundaryDistance"] = J(measurement?.BoundaryDistance),
                ["measurement"] = J(measurement), ["overlay"] = J(overlay), ["status"] = J(status), ["preview"] = J(new { width, height, count = 1, channels = 3 }) };
            var runtime = new Dictionary<string, object?> { ["centerDistance"] = measurement?.CenterDistance, ["boundaryDistance"] = measurement?.BoundaryDistance,
                ["measurement"] = measurement, ["overlay"] = overlay, ["status"] = status, ["preview"] = preview };
            preview = null; return ValueTask.FromResult(new NodeExecutionOutput(ports, runtime));
            void PaintLine(Line2D line, int[] color) => Paint(preview!, [new(line.RowBegin, line.ColumnBegin), new(line.RowEnd, line.ColumnEnd)], color);
        }
        finally { preview?.Dispose(); }

        Circle2D? ReadCircle(string key)
        {
            var hasRuntime = context.RuntimeInputs.TryGetValue(key, out var raw);
            if (hasRuntime && raw is null) return null;
            if (raw is Circle2D circle) return circle;
            if (hasRuntime && raw is not JsonElement) throw new InvalidDataException("测量端口运行值不是圆几何：" + key);
            var json = raw is JsonElement value ? value : context.Inputs.GetValueOrDefault(key);
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (json.ValueKind != JsonValueKind.Object) throw new InvalidDataException("测量端口需要圆几何：" + key);
            return json.Deserialize<Circle2D>();
        }
        void CheckCircle(Circle2D? circle, string key)
        {
            if (circle is null) return;
            if (!new[] { circle.Row, circle.Column, circle.Radius }.All(double.IsFinite) || circle.Radius <= 0 ||
                circle.Row - circle.Radius < 0 || circle.Column - circle.Radius < 0 || circle.Row + circle.Radius > height - 1 || circle.Column + circle.Radius > width - 1)
                throw new InvalidDataException("圆坐标或半径无效/越界：" + key);
            var origin = context.InputFrames?.GetValueOrDefault(key);
            if (origin is null || origin.RunId != context.RunId || origin.FrameId != frame.FrameId || origin.SpaceId != frame.SpaceId ||
                origin.SourceNodeId != frame.SourceNodeId || origin.SourcePortId != frame.SourcePortId || origin.Width != width || origin.Height != height || origin.Count != 1 || origin.ImageIndex != 1 || origin.Annotated || origin.Corrected != frame.Corrected)
                throw new InvalidDataException("MEASUREMENT_FRAME_MISMATCH：圆与原图必须来自本轮同一帧、同一坐标空间：" + key);
        }
        bool Flag(string key) => !context.Node.Parameters.TryGetValue(key, out var flag) || flag.ValueKind is JsonValueKind.True ? true :
            flag.ValueKind == JsonValueKind.False ? false : throw new InvalidDataException("测量显示开关无效：" + key);
    }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Paint(HImage image, IReadOnlyList<Point2D> points, int[] color)
    {
        using var rows = new HTuple(points.Select(p => p.Row).ToArray()); using var columns = new HTuple(points.Select(p => p.Column).ToArray());
        using var contour = new HXLDCont(); contour.GenContourPolygonXld(rows, columns);
        using var border = contour.GenRegionContourXld("margin"); using var thick = border.DilationCircle(1d); using var rgb = new HTuple(color);
        image.OverpaintRegion(thick, rgb, "fill");
    }
}
