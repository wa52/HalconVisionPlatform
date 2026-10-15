using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconSemanticSegmentationExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.SemanticSegmentation;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var cancellation = context.CancellationToken; cancellation.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage source) throw new InvalidDataException("语义分割需要连接 Image 输入。");
        var package = DlModelPackage.Load(Text(context.Node, "modelPackage", ""), cancellation, "semantic_segmentation");
        var minimum = Number(context.Node, "minimumArea", 1); var maximum = Number(context.Node, "maximumArea", 1_000_000_000);
        if (minimum < 1 || maximum < minimum) throw new InvalidDataException("区域面积范围无效。");
        var background = ParseIds(Text(context.Node, "backgroundClassIds", "0"));
        var requested = ParseIds(Text(context.Node, "targetClassIds", ""));
        if (background.Concat(requested).Any(id => !package.ClassIds.Contains(id))) throw new InvalidDataException("目标/背景类别不在模型类别列表中。");
        var targets = requested.Count > 0 ? requested : package.ClassIds.Where(id => !background.Contains(id)).ToHashSet();
        if (targets.Count == 0) throw new InvalidDataException("没有可检测的目标类别。");
        using var first = source.SelectObj(1); first.GetImageSize(out int width, out int height);
        using var channels = first.CountChannels();
        if (channels.I is not (1 or 3)) throw new InvalidDataException("分割预览仅支持灰度或 RGB 图像。");
        var rois = DlRoiSet.Read(context.Node, width, height);
        var objects = new List<SegmentationObject>(); var results = new List<RoiSegmentationResult>();
        var clock = Stopwatch.StartNew();
        HImage? masks = null, preview = null;
        HRegion? regions = null;
        var transferred = false;
        try
        {
            NodeExecutionOutput output;
            using (var session = new DlInferenceSession(package, context, "segmentation"))
            {
                masks = new HImage(); masks.GenEmptyObj();
                regions = new HRegion(); regions.GenEmptyObj();
                context.Log($"分割模型已加载，按顺序处理 {rois.Count} 个 ROI。");
                foreach (var roi in rois)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var crop = roi.Crop.Crop(first);
                        using var prediction = session.Predict(crop, cancellation, out var milliseconds);
                        using var labelsObject = prediction.GetDictObject("segmentation_image");
                        using var labels = new HImage(labelsObject);
                        using var resized = labels.ZoomImageSize(roi.Crop.Width, roi.Crop.Height, "nearest_neighbor");
                        using var bytes = resized.ConvertImageType("byte");
                        using var positioned = bytes.TileImagesOffset(roi.Crop.RowBegin, roi.Crop.ColumnBegin, -1, -1, -1, -1, width, height);
                        using var validDomain = roi.Crop.Region();
                        using var mask = positioned.ReduceDomain(validDomain);
                        var joinedMasks = masks.ConcatObj(mask); masks.Dispose(); masks = joinedMasks;
                        var classAreas = new List<SegmentationClassArea>();
                        var before = objects.Count;
                        for (var c = 0; c < package.ClassIds.Length; c++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var id = package.ClassIds[c]; var name = package.ClassNames[c];
                            using var classRegion = mask.Threshold((double)id, (double)id);
                            var pixels = classRegion.AreaCenter(out double _, out double _);
                            classAreas.Add(new(id, name, pixels, pixels / (width * (double)height)));
                            if (!targets.Contains(id)) continue;
                            using var connected = classRegion.Connection();
                            using var filtered = connected.SelectShape("area", "and", minimum, maximum);
                            for (var index = 1; index <= filtered.CountObj(); index++)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                using var component = filtered.SelectObj(index);
                                var area = component.AreaCenter(out double row, out double column);
                                component.SmallestRectangle1(out int row1, out int col1, out int row2, out int col2);
                                objects.Add(new(objects.Count + 1, id, name, area, area / (width * (double)height),
                                    new(row, column), new(row1, col1, row2, col2), roi.Id, roi.Name));
                            }
                            var joined = regions.ConcatObj(filtered); regions.Dispose(); regions = joined;
                        }
                        var count = objects.Count - before; var totalArea = objects.Skip(before).Sum(item => item.Area);
                        var status = new SegmentationStatus(count > 0 ? SegmentationStatus.Detected : SegmentationStatus.Empty,
                            $"{roi.Name}：{count} 个区域，面积 {totalArea:F0} 像素。");
                        results.Add(new(roi.Id, roi.Name, roi.Crop.Bounds, count, totalArea, classAreas, status, milliseconds));
                        context.Log($"{status.Message} 推理 {milliseconds:F1} ms。");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { throw new InvalidOperationException($"ROI“{roi.Name}”分割失败：{ex.Message}", ex); }
                }
                using var rgb = channels.I == 1 ? first.Compose3(first, first) : first.CopyImage();
                using var green = new HTuple(new int[] { 0, 255, 0 }); using var yellow = new HTuple(new int[] { 255, 220, 0 });
                preview = objects.Count > 0 ? rgb.PaintRegion(regions, green, "margin") : rgb.CopyImage();
                foreach (var roi in rois.Where(item => item.Crop.IsExplicit))
                {
                    using var boundary = roi.Crop.Region();
                    var marked = preview.PaintRegion(boundary, yellow, "margin"); preview.Dispose(); preview = marked;
                }
                var total = objects.Sum(item => item.Area);
                var statusAll = new SegmentationStatus(objects.Count > 0 ? SegmentationStatus.Detected : SegmentationStatus.Empty,
                    $"{results.Count} 个 ROI，共检出 {objects.Count} 个区域，面积累计 {total:F0} 像素。");
                var classes = results.SelectMany(item => item.Classes).GroupBy(item => item.ClassId)
                    .Select(group => new SegmentationClassArea(group.Key, group.First().ClassName, group.Sum(item => item.Pixels), group.Sum(item => item.Ratio))).ToArray();
                var ports = new Dictionary<string, JsonElement>
                {
                    ["preview"] = JsonSerializer.SerializeToElement(new { width, height, channels = 3 }),
                    ["mask"] = JsonSerializer.SerializeToElement(new { width, height, channels = 1, count = results.Count,
                        validRoi = results.Count == 1 ? results[0].Bounds : null,
                        roiIds = results.Select(item => item.RoiId).ToArray(), validRois = results.Select(item => item.Bounds).ToArray() }),
                    ["regions"] = JsonSerializer.SerializeToElement(new { width, height, count = objects.Count }),
                    ["count"] = JsonSerializer.SerializeToElement(objects.Count), ["area"] = JsonSerializer.SerializeToElement(total),
                    ["objects"] = JsonSerializer.SerializeToElement(objects), ["classes"] = JsonSerializer.SerializeToElement(classes),
                    ["roiResults"] = JsonSerializer.SerializeToElement(results), ["status"] = JsonSerializer.SerializeToElement(statusAll),
                    ["roi"] = JsonSerializer.SerializeToElement(results.Count == 1 ? results[0].Bounds : null),
                    ["modelInfo"] = JsonSerializer.SerializeToElement(new { directory = package.DirectoryPath, device = session.Runtime,
                        deviceIndex = session.DeviceIndex, roiCount = results.Count, inferenceMs = results.Sum(item => item.InferenceMilliseconds), elapsedMs = clock.Elapsed.TotalMilliseconds })
                };
                var displays = results.Select(item => new RoiDisplayResult(item.RoiId, item.RoiName, item.Bounds,
                    $"{item.Count} 个 · {item.Area:F0} px", item.Count == 0)).ToArray();
                output = new(ports, new Dictionary<string, object?> { ["preview"] = preview, ["mask"] = masks,
                    ["regions"] = regions, ["roiOverlay"] = new MultiRoiOverlay(displays) });
            }
            cancellation.ThrowIfCancellationRequested(); transferred = true;
            return ValueTask.FromResult(output);
        }
        finally
        {
            if (!transferred)
                try { preview?.Dispose(); }
                finally { try { masks?.Dispose(); } finally { regions?.Dispose(); } }
        }
    }
    private static string Text(NodeInstance node, string key, string fallback) =>
        node.Parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? fallback : fallback;
    private static double Number(NodeInstance node, string key, double fallback)
    {
        var value = node.Parameters.TryGetValue(key, out var number) ? number.GetDouble() : fallback;
        if (!double.IsFinite(value)) throw new InvalidDataException($"参数 {key} 必须为有限数值。"); return value;
    }
    private static HashSet<int> ParseIds(string text)
    {
        var ids = new HashSet<int>();
        foreach (var token in text.Split([',', '，', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id is < 0 or > 255)
                throw new InvalidDataException("类别 ID 必须为 0–255 的整数，以逗号分隔。");
            else ids.Add(id);
        return ids;
    }
}
