using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconLineCircleExecutor : INodeExecutor
{
    public string TypeId => HalconLineCircleNodeProvider.LineCircle;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var token = context.CancellationToken; token.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage image || !image.IsInitialized() || image.CountObj() != 1)
            throw new InvalidDataException("线圆测量需要本轮的一张有效原始图像。");
        image.GetImageSize(out int width, out int height);
        var frame = context.InputFrames?.GetValueOrDefault("image");
        if (context.RunId == Guid.Empty || frame is null || frame.RunId != context.RunId || frame.FrameId == Guid.Empty || frame.SpaceId == Guid.Empty ||
            frame.Annotated || frame.Count != 1 || frame.ImageIndex != 1 || frame.Width != width || frame.Height != height)
            throw new InvalidDataException("MEASUREMENT_FRAME_REQUIRED：需要本轮未标注单帧及坐标来源。");
        var line = Read<Line2D>("line"); var circle = Read<Circle2D>("circle");
        if (line is not null)
        {
            if (!new[] { line.RowBegin, line.ColumnBegin, line.RowEnd, line.ColumnEnd, line.Length }.All(double.IsFinite) || line.Length <= 1e-9 ||
                !Inside(line.RowBegin, line.ColumnBegin) || !Inside(line.RowEnd, line.ColumnEnd))
                throw new InvalidDataException("测量直线端点无效、重合或越界。");
            Origin("line");
        }
        if (circle is not null)
        {
            if (!new[] { circle.Row, circle.Column, circle.Radius }.All(double.IsFinite) || circle.Radius <= 0 ||
                !Inside(circle.Row - circle.Radius, circle.Column - circle.Radius) || !Inside(circle.Row + circle.Radius, circle.Column + circle.Radius))
                throw new InvalidDataException("测量圆坐标或半径无效/越界。");
            Origin("circle");
        }
        var showNormal = Flag("showNormal"); var showBoundary = Flag("showBoundaryLine");
        LineCircleMeasurement? measurement = null;
        var status = new LineCircleStatus(LineCircleStatus.Missing, "缺少有效直线或圆，未生成测量值；请检查上游查找结果。");
        if (line is not null && circle is not null)
        {
            var dr = line.RowEnd - line.RowBegin; var dc = line.ColumnEnd - line.ColumnBegin;
            var t = ((circle.Row - line.RowBegin) * dr + (circle.Column - line.ColumnBegin) * dc) / (dr * dr + dc * dc);
            var foot = new Point2D(line.RowBegin + t * dr, line.ColumnBegin + t * dc);
            if (!Inside(foot.Row, foot.Column)) throw new InvalidDataException("MEASUREMENT_PROJECTION_OUTSIDE：垂足位于图像外，请调整检测范围或直线方向。");
            var box = new Rectangle2D(Math.Min(circle.Row - circle.Radius, Math.Min(foot.Row, Math.Min(line.RowBegin, line.RowEnd))),
                Math.Min(circle.Column - circle.Radius, Math.Min(foot.Column, Math.Min(line.ColumnBegin, line.ColumnEnd))),
                Math.Max(circle.Row + circle.Radius, Math.Max(foot.Row, Math.Max(line.RowBegin, line.RowEnd))),
                Math.Max(circle.Column + circle.Radius, Math.Max(foot.Column, Math.Max(line.ColumnBegin, line.ColumnEnd))));
            if (context.Node.Parameters.TryGetValue("taskRoi", out var scoped))
            {
                var scopedNode = context.Node with { Parameters = new(context.Node.Parameters) { ["roi"] = scoped } };
                var bounds = TaskRoiScope.Read(scopedNode, width, height) ?? throw new InvalidDataException("测量任务区域为空。");
                if (box.RowBegin < bounds.RowBegin || box.ColumnBegin < bounds.ColumnBegin || box.RowEnd > bounds.RowEnd || box.ColumnEnd > bounds.ColumnEnd)
                    throw new InvalidDataException("MEASUREMENT_OUTSIDE_TASK：直线、完整圆和垂足必须位于测量任务区域内。");
            }
            ValidRegionGuard.Require(context, image, [box]);
            using (var domain = image.GetDomain())
            using (var requested = new HRegion(Math.Floor(box.RowBegin), Math.Floor(box.ColumnBegin), Math.Ceiling(box.RowEnd), Math.Ceiling(box.ColumnEnd)))
            using (var missing = requested.Difference(domain))
            using (var area = missing.AreaCenter(out HTuple rows, out HTuple cols))
            using (rows) using (cols) using (var real = area.TupleReal())
                if (real.DArr.Sum() > 0) throw new InvalidDataException("MEASUREMENT_INVALID_PIXELS：线圆测量的包围范围包含无效像素。");
            token.ThrowIfCancellationRequested();
            var distance = HMisc.DistancePl(circle.Row, circle.Column, line.RowBegin, line.ColumnBegin, line.RowEnd, line.ColumnEnd);
            if (!double.IsFinite(distance) || distance < 0) throw new InvalidDataException("HALCON 返回无效垂距。");
            var clearance = distance - circle.Radius; var gap = Math.Max(0, clearance);
            var epsilon = 1e-9 * Math.Max(1, Math.Max(distance, circle.Radius));
            var relation = Math.Abs(clearance) <= epsilon ? "相切" : clearance > 0 ? "相离" : "相交";
            Line2D? boundary = gap <= epsilon ? null : new(circle.Row + (foot.Row - circle.Row) * circle.Radius / distance,
                circle.Column + (foot.Column - circle.Column) * circle.Radius / distance, foot.Row, foot.Column);
            measurement = new(line, circle, foot, t >= 0 && t <= 1, distance, gap, clearance, relation,
                new(circle.Row, circle.Column, foot.Row, foot.Column), boundary);
            status = new(LineCircleStatus.Measured, "线圆测量完成 · " + relation + (measurement.ProjectionWithinSegment ? "" : " · 垂足在拟合线段延长线上"));
        }
        context.Log(status.Message); token.ThrowIfCancellationRequested();
        HImage? preview = null;
        try
        {
            using var owned = new OwnedPreviewImage(image, token); preview = owned.TakeImage();
            if (measurement is not null)
            {
                Paint(preview, circle!.Outline(), [50, 205, 50]); PaintLine(line!, [50, 205, 50]);
                if (!measurement.ProjectionWithinSegment)
                {
                    var end = HMisc.DistancePp(measurement.Projection.Row, measurement.Projection.Column, line!.RowBegin, line.ColumnBegin) <
                        HMisc.DistancePp(measurement.Projection.Row, measurement.Projection.Column, line.RowEnd, line.ColumnEnd)
                        ? new Point2D(line.RowBegin, line.ColumnBegin) : new Point2D(line.RowEnd, line.ColumnEnd);
                    PaintLine(new(end.Row, end.Column, measurement.Projection.Row, measurement.Projection.Column), [145, 160, 170]);
                }
                if (showNormal) PaintLine(measurement.CenterNormal, [0, 210, 255]);
                if (showBoundary && measurement.BoundaryLine is { } boundary) PaintLine(boundary, [255, 210, 0]);
            }
            token.ThrowIfCancellationRequested();
            var overlay = new LineCircleOverlay(measurement, showNormal, showBoundary);
            var ports = new Dictionary<string, JsonElement> { ["centerToLine"] = J(measurement?.CenterToLine), ["boundaryToLine"] = J(measurement?.BoundaryToLine),
                ["signedClearance"] = J(measurement?.SignedClearance), ["measurement"] = J(measurement), ["overlay"] = J(overlay),
                ["status"] = J(status), ["preview"] = J(new { width, height, count = 1, channels = 3 }) };
            var runtime = new Dictionary<string, object?> { ["centerToLine"] = measurement?.CenterToLine, ["boundaryToLine"] = measurement?.BoundaryToLine,
                ["signedClearance"] = measurement?.SignedClearance, ["measurement"] = measurement, ["overlay"] = overlay, ["status"] = status, ["preview"] = preview };
            preview = null; return ValueTask.FromResult(new NodeExecutionOutput(ports, runtime));
            void PaintLine(Line2D segment, int[] color) { if (segment.Length > 1e-12) Paint(preview!, [new(segment.RowBegin, segment.ColumnBegin), new(segment.RowEnd, segment.ColumnEnd)], color); }
        }
        finally { preview?.Dispose(); }

        bool Inside(double row, double col) => double.IsFinite(row) && double.IsFinite(col) && row >= 0 && col >= 0 && row <= height - 1 && col <= width - 1;
        T? Read<T>(string key) where T : class
        {
            var present = context.RuntimeInputs.TryGetValue(key, out var raw);
            if (present && raw is null) return null;
            if (raw is T typed) return typed;
            if (present && raw is not JsonElement) throw new InvalidDataException("测量端口类型错误：" + key);
            var json = raw is JsonElement value ? value : context.Inputs.GetValueOrDefault(key);
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (json.ValueKind != JsonValueKind.Object) throw new InvalidDataException("测量端口需要几何数据：" + key);
            return json.Deserialize<T>();
        }
        void Origin(string key)
        {
            var origin = context.InputFrames?.GetValueOrDefault(key);
            if (origin is null || origin.RunId != context.RunId || origin.FrameId != frame.FrameId || origin.SpaceId != frame.SpaceId ||
                origin.SourceNodeId != frame.SourceNodeId || origin.SourcePortId != frame.SourcePortId || origin.Width != width || origin.Height != height ||
                origin.Count != 1 || origin.ImageIndex != 1 || origin.Annotated || origin.Corrected != frame.Corrected)
                throw new InvalidDataException("MEASUREMENT_FRAME_MISMATCH：直线、圆与原图必须来自本轮同帧及坐标空间：" + key);
        }
        bool Flag(string key) => !context.Node.Parameters.TryGetValue(key, out var value) ? true : value.ValueKind == JsonValueKind.True ? true :
            value.ValueKind == JsonValueKind.False ? false : throw new InvalidDataException("测量显示开关无效：" + key);
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
