using System.Globalization;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconCircleFindExecutor : INodeExecutor
{
    public string TypeId => HalconCircleNodeProvider.CircleFind;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var cancellation = context.CancellationToken; cancellation.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage image || !image.IsInitialized())
            throw new InvalidDataException("圆查找需要连接有效的 HALCON 图像输入。");
        if (image.CountObj() != 1) throw new InvalidDataException("圆查找每次处理一张图像；批量请使用文件夹逐张运行。");
        if (context.InputFrames?.GetValueOrDefault("image")?.Annotated == true)
            throw new InvalidDataException("圆查找必须使用未标注图像，不能把检测预览当作原图。");
        image.GetImageSize(out int width, out int height);
        var requested = TaskRoiScope.Read(context.Node, width, height)
            ?? throw new InvalidDataException("请先绘制包含完整待测圆的矩形范围，再运行圆查找。");
        var roi = new Rectangle2D(Math.Ceiling(requested.RowBegin), Math.Ceiling(requested.ColumnBegin), Math.Floor(requested.RowEnd), Math.Floor(requested.ColumnEnd));
        ValidRegionGuard.Require(context, image, [requested]);
        RequireFullDomain(image, roi);

        var searchWidth = Number(context.Node, "searchHalfWidth", 10, .5, 10000);
        var tangent = Number(context.Node, "measureLength2", 3, .5, 1000);
        var sigma = Number(context.Node, "measureSigma", 1, .4, 100);
        var threshold = Number(context.Node, "measureThreshold", 20, .001, 65535);
        var minScore = Number(context.Node, "minScore", .6, .01, 1);
        var measures = Number(context.Node, "numMeasures", 48, 8, 1000);
        if (measures != Math.Truncate(measures)) throw new InvalidDataException("测量卡尺数量必须是整数。");
        var minimumRadius = Number(context.Node, "minimumRadius", 1, .1, 100000);
        var maximumRadius = Number(context.Node, "maximumRadius", 100000, .1, 100000);
        if (minimumRadius > maximumRadius) throw new InvalidDataException("最小半径不能大于最大半径。");
        var maxResidual = Number(context.Node, "maximumResidual", 2, .001, 1000);
        var referenceRadius = Number(context.Node, "referenceRadius", 0, 0, 100000);
        var transition = Text(context.Node, "measureTransition", "all");
        if (transition is not ("all" or "positive" or "negative")) throw new InvalidDataException("边缘极性必须选择全部、暗到亮或亮到暗。");
        var showRoi = Boolean(context.Node, "showSearchRegion", true);
        var showFit = Boolean(context.Node, "showFittedCircle", true);
        var diagnostics = Boolean(context.Node, "showDiagnostics", false);

        var localRow = (roi.RowEnd - roi.RowBegin) / 2;
        var localColumn = (roi.ColumnEnd - roi.ColumnBegin) / 2;
        var outerLimit = Math.Min(localRow, localColumn) - 1;
        if (referenceRadius == 0 && outerLimit > tangent)
            referenceRadius = Math.Sqrt(outerLimit * outerLimit - tangent * tangent) - searchWidth;
        if (referenceRadius <= searchWidth + 2 || outerLimit <= 0 ||
            Math.Sqrt(Math.Pow(referenceRadius + searchWidth, 2) + tangent * tangent) > outerLimit + 1e-8 || sigma > searchWidth / 2)
            throw new InvalidDataException("搜索框过小或参考半径/卡尺越出范围。请扩大矩形框，或缩小搜索半宽、切向半长与平滑 σ。");

        using var channelCount = image.CountChannels();
        if (channelCount.I is not (1 or 3)) throw new InvalidDataException("圆查找仅支持单通道或 RGB 图像。");
        using var crop = image.CropRectangle1(roi.RowBegin, roi.ColumnBegin, roi.RowEnd, roi.ColumnEnd);
        using var gray = channelCount.I == 3 ? crop.Rgb1ToGray() : null;
        var measurementImage = gray ?? crop;
        using var type = measurementImage.GetImageType();
        if (type.S is not ("byte" or "uint2" or "real")) throw new InvalidDataException("圆查找支持 byte、uint2 或 real 图像。");
        Circle2D? circle = null; double? score = null; double? residual = null;
        var points = new PointSet([]);
        IReadOnlyList<IReadOnlyList<Point2D>> measurePolygons = [];
        var status = new CircleFindStatus(CircleFindStatus.NotFound, "未找到圆，请检查搜索范围、参考半径、边缘极性和阈值。");
        HImage? preview = null;
        try
        {
            using var model = new HMetrologyModel();
            measurementImage.GetImageSize(out int cropWidth, out int cropHeight);
            model.SetMetrologyModelImageSize(cropWidth, cropHeight);
            using var noNames = new HTuple(); using var noValues = new HTuple();
            var index = model.AddMetrologyObjectCircleMeasure(localRow, localColumn, referenceRadius, searchWidth, tangent, sigma, threshold, noNames, noValues);
            using var objectIndex = new HTuple(index);
            using var names = new HTuple(new[] { "num_measures", "measure_transition", "measure_select", "min_score", "num_instances" });
            using var values = new HTuple(new object[] { (int)measures, transition, "all", minScore, 1 });
            model.SetMetrologyObjectParam(objectIndex, names, values);
            context.Log("HALCON 圆测量：开始测量框内的边缘。");
            cancellation.ThrowIfCancellationRequested();
            // HALCON ignores reduced domains here. A physical ROI crop bounds the measurement image.
            model.ApplyMetrologyModel(measurementImage);
            cancellation.ThrowIfCancellationRequested();
            if (model.GetMetrologyObjectNumInstances(index) > 0)
            {
                var candidate = new Circle2D(Result(model, index, "row") + roi.RowBegin, Result(model, index, "column") + roi.ColumnBegin, Result(model, index, "radius"));
                score = Result(model, index, "score");
                using var used = new HTuple("used_edges"); using var rowName = new HTuple("row"); using var columnName = new HTuple("column");
                using var rows = model.GetMetrologyObjectResult(index, "all", used, rowName);
                using var columns = model.GetMetrologyObjectResult(index, "all", used, columnName);
                if (rows.Length != columns.Length) throw new InvalidDataException("HALCON 返回的边缘坐标数量不一致。");
                points = new(Enumerable.Range(0, rows.Length).Select(i => new Point2D(rows[i].D + roi.RowBegin, columns[i].D + roi.ColumnBegin)).ToArray());
                if (points.Points.Any(p => !double.IsFinite(p.Row) || !double.IsFinite(p.Column))) throw new InvalidDataException("HALCON 返回无效边缘点。");
                residual = points.Points.Count > 0 ? Math.Sqrt(points.Points.Average(p => Math.Pow(Math.Sqrt(Math.Pow(p.Row - candidate.Row, 2) + Math.Pow(p.Column - candidate.Column, 2)) - candidate.Radius, 2))) : null;
                var contained = candidate.Row - candidate.Radius >= roi.RowBegin && candidate.Column - candidate.Radius >= roi.ColumnBegin &&
                    candidate.Row + candidate.Radius <= roi.RowEnd && candidate.Column + candidate.Radius <= roi.ColumnEnd;
                var offset = Math.Sqrt(Math.Pow(candidate.Row - (localRow + roi.RowBegin), 2) + Math.Pow(candidate.Column - (localColumn + roi.ColumnBegin), 2));
                if (points.Points.Count >= 3 && candidate.Radius >= minimumRadius && candidate.Radius <= maximumRadius &&
                    Math.Abs(candidate.Radius - referenceRadius) <= searchWidth && contained && offset <= searchWidth && score >= minScore && residual <= maxResidual)
                {
                    circle = candidate;
                    status = new(CircleFindStatus.Found, $"找到圆 · 直径 {candidate.Diameter.ToString("F2", CultureInfo.InvariantCulture)} px · {points.Points.Count} 个实际边缘点。");
                }
                else status = new(CircleFindStatus.NotFound, "圆拟合未通过范围/圆心偏移、半径、边缘利用率或残差校验；几何结果为空，请查看评分与残差。");
            }
            if (diagnostics)
            {
                using var contours = model.GetMetrologyObjectMeasures(objectIndex, transition, out var rows, out var columns);
                using (rows) using (columns) measurePolygons = ReadContours(contours, roi);
            }
            var overlay = new CircleGeometryOverlay(roi, circle, points, measurePolygons, showRoi, showFit, diagnostics);
            using var owned = new OwnedPreviewImage(image, cancellation);
            preview = owned.TakeImage(); // Still owned privately by this executor until the complete output is returned.
            if (diagnostics)
            {
                foreach (var polygon in measurePolygons) Paint(preview, polygon, [30, 144, 255], .5);
                foreach (var point in points.Points) Paint(preview, [new(point.Row - 2, point.Column), new(point.Row + 2, point.Column)], [50, 205, 50], .5);
            }
            if (showRoi) Paint(preview, Rectangle(roi), circle is null ? [255, 0, 0] : [255, 220, 0], 1);
            if (showFit && circle is not null)
            {
                Paint(preview, circle.Outline(), [50, 205, 50], 1.5);
                Paint(preview, [new(circle.Row - 5, circle.Column), new(circle.Row + 5, circle.Column)], [50, 205, 50], 1);
                Paint(preview, [new(circle.Row, circle.Column - 5), new(circle.Row, circle.Column + 5)], [50, 205, 50], 1);
            }
            cancellation.ThrowIfCancellationRequested();
            var count = circle is null ? 0 : 1;
            context.Log($"HALCON 圆查找：{status.Code}，卡尺 {measures}，边缘 {points.Points.Count}，评分 {score?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"}。");
            cancellation.ThrowIfCancellationRequested();
            var serialized = new Dictionary<string, JsonElement>
            {
                ["circle"] = J(circle), ["diameter"] = J(circle?.Diameter), ["score"] = J(score), ["residualRms"] = J(residual),
                ["edgePoints"] = J(points), ["count"] = J(count), ["status"] = J(status), ["overlay"] = J(overlay),
                ["preview"] = J(new { count = 1, width, height, channels = 3 })
            };
            var runtime = new Dictionary<string, object?> { ["circle"] = circle, ["diameter"] = circle?.Diameter, ["score"] = score,
                ["residualRms"] = residual, ["edgePoints"] = points, ["count"] = count, ["status"] = status, ["overlay"] = overlay, ["preview"] = preview };
            preview = null;
            return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        finally { preview?.Dispose(); }
    }

    private static void RequireFullDomain(HImage image, Rectangle2D roi)
    {
        using var domain = image.GetDomain(); using var rectangle = new HRegion(roi.RowBegin, roi.ColumnBegin, roi.RowEnd, roi.ColumnEnd);
        using var missing = rectangle.Difference(domain);
        using var area = missing.AreaCenter(out HTuple rows, out HTuple columns); using var rowScope = rows; using var columnScope = columns; using var real = area.TupleReal();
        if (real.DArr.Sum() > 0) throw new InvalidDataException("圆查找范围包含无效像素，请把完整矩形放在图像有效区域内。");
    }
    private static double Result(HMetrologyModel model, int index, string key)
    {
        using var type = new HTuple("result_type"); using var name = new HTuple(key);
        using var result = model.GetMetrologyObjectResult(index, "all", type, name);
        if (result.Length != 1 || !double.IsFinite(result[0].D)) throw new InvalidDataException("HALCON 圆参数无效：" + key);
        return result[0].D;
    }
    private static IReadOnlyList<IReadOnlyList<Point2D>> ReadContours(HXLDCont contours, Rectangle2D roi)
    {
        var result = new List<IReadOnlyList<Point2D>>();
        for (var i = 1; i <= contours.CountObj(); i++)
        {
            using var contour = contours.SelectObj(i); contour.GetContourXld(out HTuple rows, out HTuple columns);
            using (rows) using (columns) result.Add(Enumerable.Range(0, rows.Length).Select(j => new Point2D(rows[j].D + roi.RowBegin, columns[j].D + roi.ColumnBegin)).ToArray());
        }
        return result;
    }
    private static IReadOnlyList<Point2D> Rectangle(Rectangle2D b) => [new(b.RowBegin, b.ColumnBegin), new(b.RowBegin, b.ColumnEnd), new(b.RowEnd, b.ColumnEnd), new(b.RowEnd, b.ColumnBegin), new(b.RowBegin, b.ColumnBegin)];
    private static void Paint(HImage owned, IReadOnlyList<Point2D> points, int[] rgb, double thickness)
    {
        if (points.Count < 2) return;
        using var rows = new HTuple(points.Select(p => p.Row).ToArray()); using var columns = new HTuple(points.Select(p => p.Column).ToArray());
        using var contour = new HXLDCont(); contour.GenContourPolygonXld(rows, columns);
        using var border = contour.GenRegionContourXld("margin"); using var region = border.DilationCircle(thickness); using var color = new HTuple(rgb);
        owned.OverpaintRegion(region, color, "fill");
    }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static double Number(NodeInstance node, string key, double fallback, double minimum, double maximum)
    {
        if (!node.Parameters.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new InvalidDataException($"圆查找参数 {key} 必须在 {minimum}–{maximum} 之间。");
        return number;
    }
    private static string Text(NodeInstance node, string key, string fallback) => !node.Parameters.TryGetValue(key, out var value) ? fallback :
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : throw new InvalidDataException("圆查找参数必须为文本：" + key);
    private static bool Boolean(NodeInstance node, string key, bool fallback) => !node.Parameters.TryGetValue(key, out var value) ? fallback :
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidDataException("圆查找参数必须为开关：" + key);
}
