using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconQuickMatchExecutor(ShapeMatchModelCache? cache = null) : INodeExecutor
{
    public string TypeId => HalconNodeProvider.QuickMatch;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage images || !images.IsInitialized())
            throw new InvalidDataException("快捷匹配需要连接有效的 HALCON 图像输入。");
        var count = images.CountObj();
        if (count < 1) throw new InvalidDataException("输入图像集合为空。");
        var score = QuickMatchParameters.Number(context.Node, "minimumScore", .6, .001, 1);
        var start = QuickMatchParameters.Number(context.Node, "angleStart", -180, -180, 180);
        var extent = QuickMatchParameters.Number(context.Node, "angleExtent", 360, 0, 360);
        if (start + extent > 180.000001) throw new InvalidDataException("起始角度加角度范围不能超过 180°。");
        var maximum = QuickMatchParameters.Integer(context.Node, "maximumMatches", 1, 1, 100);
        var overlap = QuickMatchParameters.Number(context.Node, "maximumOverlap", .5, 0, 1);
        var greediness = QuickMatchParameters.Number(context.Node, "greediness", .8, 0, 1);
        var timeout = QuickMatchParameters.Integer(context.Node, "timeoutMs", 5000, 1, 60000);
        using var fallback = cache is null ? new ShapeMatchModelCache() : null;
        using var lease = (cache ?? fallback!).Acquire(context);
        lease.Model.SetShapeModelParam("timeout", timeout);
        var results = new List<QuickMatchImageResult>();
        var overlays = new List<QuickMatchFrameOverlay>();
        HImage? previews = null;
        var width = 0; var height = 0;
        try
        {
            for (var index = 1; index <= count; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var image = images.SelectObj(index); using var gray = QuickMatchParameters.Gray(image);
                gray.GetImageSize(out int w, out int h);
                if (index == 1) { width = w; height = h; }
                else if (w != width || h != height) throw new InvalidDataException("同次批量输入的图像尺寸必须一致；不同尺寸可使用文件夹逐张运行。");
                var roi = DlCropRoi.Read(context.Node, w, h);
                using var region = roi.Region(); using var search = gray.ReduceDomain(region);
                search.FindShapeModel(lease.Model, start * Math.PI / 180, extent * Math.PI / 180,
                    score, maximum, overlap, "least_squares", 0, greediness,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scores);
                using (rows) using (columns) using (angles) using (scores)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var poses = new List<QuickMatchPose>(); var graphics = new List<QuickMatchGraphic>();
                    using var allContours = new HXLDCont(); allContours.GenEmptyObj();
                    HXLDCont? accumulated = null;
                    try
                    {
                        for (var match = 0; match < scores.Length; match++)
                        {
                            var pose = new QuickMatchPose(rows[match].D, columns[match].D, angles[match].D * 180 / Math.PI, scores[match].D);
                            poses.Add(pose);
                            var transform = new HHomMat2D(); transform.VectorAngleToRigid(0d, 0d, 0d, pose.Row, pose.Column, angles[match].D);
                            using var contour = lease.Contours.AffineTransContourXld(transform);
                            graphics.Add(new(pose, Snapshot(contour)));
                            var combined = (accumulated ?? allContours).ConcatObj(contour);
                            accumulated?.Dispose(); accumulated = combined;
                        }
                        results.Add(new(index, poses));
                        overlays.Add(new(index, roi.IsExplicit ? roi.Bounds : null, graphics));
                        using var preview = PaintPreview(gray, accumulated ?? allContours, roi.IsExplicit ? region : null);
                        var next = previews is null ? preview.CopyImage() : previews.ConcatObj(preview);
                        previews?.Dispose(); previews = next;
                    }
                    finally { accumulated?.Dispose(); }
                }
            }
            var total = results.Sum(result => result.Matches.Count);
            var foundImages = results.Count(result => result.Matches.Count > 0);
            var status = new QuickMatchStatus(total == 0 ? QuickMatchStatus.NotFound : foundImages == count ? QuickMatchStatus.Found : QuickMatchStatus.Partial,
                total == 0 ? "未找到模板目标" : $"{count} 张图像中 {foundImages} 张找到目标，共 {total} 个匹配");
            var best = count == 1 ? results[0].Matches.MaxBy(pose => pose.Score) : null;
            var ports = new Dictionary<string, JsonElement>
            {
                ["preview"] = JsonSerializer.SerializeToElement(new { width, height, count }),
                ["matches"] = JsonSerializer.SerializeToElement(count == 1 ? results[0].Matches : Array.Empty<QuickMatchPose>()),
                ["imageResults"] = JsonSerializer.SerializeToElement(results), ["count"] = JsonSerializer.SerializeToElement(total),
                ["row"] = JsonSerializer.SerializeToElement(best?.Row), ["column"] = JsonSerializer.SerializeToElement(best?.Column),
                ["angle"] = JsonSerializer.SerializeToElement(best?.AngleDegrees), ["score"] = JsonSerializer.SerializeToElement(best?.Score),
                ["status"] = JsonSerializer.SerializeToElement(status),
                ["overlay"] = JsonSerializer.SerializeToElement(new { images = count, matches = total }),
                ["modelInfo"] = JsonSerializer.SerializeToElement(new { cacheHit = lease.CacheHit, templateImage = QuickMatchParameters.TemplatePath(context.Node), method = "HALCON shape matching" })
            };
            var runtime = new Dictionary<string, object?> { ["preview"] = previews, ["overlay"] = new QuickMatchOverlay(overlays) };
            previews = null; lease.Complete();
            context.Log($"快捷匹配：{status.Message}；模型{(lease.CacheHit ? "复用" : "新建")}。");
            return ValueTask.FromResult(new NodeExecutionOutput(ports, runtime));
        }
        finally { previews?.Dispose(); }
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> Snapshot(HXLDCont contours)
    {
        var result = new List<IReadOnlyList<Point2D>>();
        for (var index = 1; index <= contours.CountObj(); index++)
        {
            using var contour = contours.SelectObj(index); contour.GetContourXld(out HTuple rows, out HTuple cols);
            using (rows) using (cols)
                result.Add(Enumerable.Range(0, rows.Length).Select(i => new Point2D(rows[i].D, cols[i].D)).ToArray());
        }
        return result;
    }
    private static HImage PaintPreview(HImage gray, HXLDCont contours, HRegion? searchRegion)
    {
        using var bytes = gray.ConvertImageType("byte");
        using var boundary = searchRegion?.Boundary("inner");
        HImage red = bytes.CopyImage(), green = bytes.CopyImage(), blue = bytes.CopyImage();
        try
        {
            if (boundary is not null)
            { Paint(ref red, boundary, 255); Paint(ref green, boundary, 220); Paint(ref blue, boundary, 0); }
            if (contours.CountObj() > 0)
            {
                using var regions = contours.GenRegionContourXld("margin"); using var thick = regions.DilationCircle(1.5);
                Paint(ref red, thick, 0); Paint(ref green, thick, 255); Paint(ref blue, thick, 0);
            }
            return red.Compose3(green, blue);
        }
        finally { red.Dispose(); green.Dispose(); blue.Dispose(); }
        static void Paint(ref HImage image, HRegion region, double value)
        { var painted = image.PaintRegion(region, value, "fill"); image.Dispose(); image = painted; }
    }
}

internal static class QuickMatchParameters
{
    public static string TemplatePath(NodeInstance node)
    {
        if (!node.Parameters.TryGetValue("templateImage", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("请先选择模板图像，或使用“取当前图像为模板”。");
        var path = Path.GetFullPath(value.GetString()!);
        if (!File.Exists(path)) throw new FileNotFoundException("模板图像不存在。", path);
        return path;
    }
    public static HImage Gray(HImage image)
    {
        using var channels = image.CountChannels();
        return channels.I switch { 1 => image.CopyImage(), 3 => image.Rgb1ToGray(), _ => throw new InvalidDataException("快捷匹配只支持单通道或 RGB 三通道图像。") };
    }
    public static double Number(NodeInstance node, string key, double fallback, double minimum, double maximum)
    {
        var number = node.Parameters.TryGetValue(key, out var value) ? value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : double.NaN : fallback;
        if (!double.IsFinite(number) || number < minimum || number > maximum) throw new InvalidDataException($"快捷匹配参数 {key} 必须在 {minimum}～{maximum} 范围内。");
        return number;
    }
    public static int Integer(NodeInstance node, string key, int fallback, int minimum, int maximum)
    {
        var number = Number(node, key, fallback, minimum, maximum);
        if (number != Math.Truncate(number)) throw new InvalidDataException($"快捷匹配参数 {key} 必须是整数。");
        return (int)number;
    }
    public static bool Boolean(NodeInstance node, string key, bool fallback) => !node.Parameters.TryGetValue(key, out var value) ? fallback
        : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidDataException($"快捷匹配参数 {key} 必须是布尔值。");
}
