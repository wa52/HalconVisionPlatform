using System.Diagnostics;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

/// <summary>Full-domain rectangle1 inference. Native outputs transfer only after all ROIs complete.</summary>
public sealed class HalconObjectDetectionExecutor(DlModelSessionCache? cache = null) : INodeExecutor
{
    public string TypeId => HalconNodeProvider.ObjectDetection;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var cancellation = context.CancellationToken; cancellation.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage source) throw new InvalidDataException("目标检测需要连接图像输入。");
        string Text(string key, string fallback) => context.Node.Parameters.TryGetValue(key, out var value) ? value.GetString()?.Trim() ?? "" : fallback;
        double Number(string key, double fallback) => context.Node.Parameters.TryGetValue(key, out var value) ? value.GetDouble() : fallback;
        int Integer(string key, int fallback, int minimum, int maximum)
        {
            var number = Number(key, fallback);
            if (!double.IsFinite(number) || number < minimum || number > maximum || number != Math.Truncate(number))
                throw new InvalidDataException($"参数 {key} 必须为 {minimum}～{maximum} 的整数。");
            return (int)number;
        }
        var threshold = Number("minimumConfidence", .5); var overlap = Number("maxOverlap", .5);
        if (!double.IsFinite(threshold) || threshold <= 0 || threshold > 1 || !double.IsFinite(overlap) || overlap < 0 || overlap > 1)
            throw new InvalidDataException("置信度需为 (0,1]，NMS 重叠阈值需为 [0,1]。");
        var maximum = Integer("maxDetections", 100, 1, 1000);
        var minimumCount = Integer("minimumCount", 1, 0, 1000); var maximumCount = Integer("maximumCount", 1, 0, 1000);
        if (maximumCount < minimumCount) throw new InvalidDataException("最大目标数量不能小于最小数量。");
        var expected = Text("expectedClass", ""); var ngPrefix = Text("ngClassPrefix", "NG");
        if (ngPrefix.Length == 0) throw new InvalidDataException("NG 类别前缀不能为空。");
        using var first = source.SelectObj(1); first.GetImageSize(out int width, out int height);
        using var channels = first.CountChannels();
        if (channels.I is not (1 or 3)) throw new InvalidDataException("目标检测仅支持灰度/RGB 图像。");
        var rois = DlRoiSet.Read(context.Node, width, height); ValidRegionGuard.Require(context, first, rois.Select(roi => roi.Crop.Bounds)); var clock = Stopwatch.StartNew();
        var results = new List<RoiObjectDetectionResult>(); var labels = new List<RoiDisplayResult>();
        HImage? preview = null; var transferred = false;
        try
        {
            NodeExecutionOutput output;
            using (var lease = DlSessionLease.Open(cache, Text("modelPackage", ""), context, "detection", "detection"))
            {
                var package = lease.Package; var session = lease.Session;
                // Model parameters are mutable: set every option while holding the exclusive lease.
                session.ConfigureDetection(threshold, overlap, maximum);
                foreach (var roi in rois)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var required = expected == "$roi" ? roi.Name : expected;
                    if (required.Length > 0 && !package.ClassNames.Contains(required, StringComparer.Ordinal))
                        throw new InvalidDataException($"ROI {roi.Name} 的期望类别 {required} 不在模型中。");
                    using var crop = roi.Crop.Crop(first);
                    using var prediction = session.Predict(crop, cancellation, out var milliseconds);
                    var candidates = Decode(prediction, package, roi, session.DetectionWidth, session.DetectionHeight);
                    var accepted = candidates.Where(item => item.Confidence >= threshold &&
                        (required.Length == 0 || item.ClassName == required || item.ClassName.StartsWith(ngPrefix, StringComparison.OrdinalIgnoreCase))).ToArray();
                    var ng = accepted.Count(item => item.ClassName.StartsWith(ngPrefix, StringComparison.OrdinalIgnoreCase));
                    var normal = accepted.Length - ng;
                    var code = ng > 0 ? ClassificationDecision.DetectedNg : normal >= minimumCount && normal <= maximumCount
                        ? ClassificationDecision.Ok : ClassificationDecision.NotDetected;
                    var judgement = new ClassificationDecision(code, ng > 0 ? $"检出 NG {ng} 个" : code == ClassificationDecision.Ok
                        ? $"OK · 检出 {normal} 个" : normal == 0 ? "NG · 未检出期望目标" : $"NG · 数量异常 {normal} 个");
                    results.Add(new(roi.Id, roi.Name, roi.Crop.Bounds, required.Length == 0 ? null : required, accepted, candidates, judgement, milliseconds));
                    labels.Add(new(roi.Id, roi.Name, roi.Crop.Bounds, judgement.Message, code != ClassificationDecision.Ok, judgement));
                    foreach (var item in accepted)
                    {
                        var itemCode = item.ClassName.StartsWith(ngPrefix, StringComparison.OrdinalIgnoreCase) ? ClassificationDecision.DetectedNg : code;
                        labels.Add(new(roi.Id, roi.Name, item.Bounds, $"{(itemCode == ClassificationDecision.Ok ? "OK" : itemCode == ClassificationDecision.DetectedNg ? "检出 NG" : "NG")} · {item.ClassName} · {item.Confidence:P1}",
                            itemCode != ClassificationDecision.Ok, new(itemCode, judgement.Message)));
                    }
                    context.Log($"{roi.Name}：{judgement.Message}；候选 {candidates.Length}，接受 {accepted.Length}；推理 {milliseconds:F1} ms。");
                }
                using var previewBuilder = new OwnedPreviewImage(first, cancellation);
                foreach (var label in labels)
                {
                    var box = label.Bounds;
                    using var region = new HRegion(box.RowBegin, box.ColumnBegin, box.RowEnd, box.ColumnEnd);
                    using var color = new HTuple(label.Decision!.Code switch
                    {
                        ClassificationDecision.Ok => new int[] { 50, 205, 50 },
                        ClassificationDecision.DetectedNg => new int[] { 30, 144, 255 }, _ => new int[] { 255, 0, 0 }
                    });
                    previewBuilder.PaintMargin(region, color);
                }
                preview = previewBuilder.TakeImage();
                var ok = results.Count(item => item.Decision.Code == ClassificationDecision.Ok);
                var missing = results.Count(item => item.Decision.Code == ClassificationDecision.NotDetected);
                var summary = new ClassificationDecision(missing > 0 ? ClassificationDecision.NotDetected : ok == results.Count ? ClassificationDecision.Ok : ClassificationDecision.DetectedNg,
                    $"OK {ok} 个区域；未检出/数量异常 NG {missing} 个；检出 NG {results.Count - ok - missing} 个");
                var objects = results.SelectMany(item => item.Objects).ToArray();
                var ports = new Dictionary<string, JsonElement>
                {
                    ["preview"] = JsonSerializer.SerializeToElement(new { width, height, channels = 3 }),
                    ["objects"] = JsonSerializer.SerializeToElement(objects), ["count"] = JsonSerializer.SerializeToElement(objects.Length),
                    ["roiResults"] = JsonSerializer.SerializeToElement(results), ["decision"] = JsonSerializer.SerializeToElement(summary),
                    ["status"] = JsonSerializer.SerializeToElement(new { Code = objects.Length == 0 ? "EMPTY" : "DETECTED", Message = summary.Message }),
                    ["modelInfo"] = JsonSerializer.SerializeToElement(new { directory = package.DirectoryPath, device = session.Runtime, deviceIndex = session.DeviceIndex,
                        roiCount = results.Count, cacheHit = lease.CacheHit, modelLoadMs = lease.LoadMilliseconds, inputWidth = session.DetectionWidth, inputHeight = session.DetectionHeight,
                        minimumConfidence = threshold, maxOverlap = overlap, maximum, inferenceMs = results.Sum(item => item.InferenceMilliseconds), elapsedMs = clock.Elapsed.TotalMilliseconds,
                        countPolicy = "逐ROI计数；重叠ROI可能重复计数", validationStatus = package.ValidationStatus })
                };
                output = new(ports, new Dictionary<string, object?> { ["preview"] = preview, ["roiOverlay"] = new MultiRoiOverlay(labels) });
                cancellation.ThrowIfCancellationRequested(); lease.Complete();
            }
            cancellation.ThrowIfCancellationRequested(); transferred = true; return ValueTask.FromResult(output);
        }
        finally { if (!transferred) preview?.Dispose(); }
    }

    private static DetectedObject[] Decode(HDict prediction, DlModelPackage package, NamedDlRoi roi, int width, int height)
    {
        using var r1 = prediction.GetDictTuple("bbox_row1"); using var c1 = prediction.GetDictTuple("bbox_col1");
        using var r2 = prediction.GetDictTuple("bbox_row2"); using var c2 = prediction.GetDictTuple("bbox_col2");
        using var ids = prediction.GetDictTuple("bbox_class_id"); using var scores = prediction.GetDictTuple("bbox_confidence");
        if (new[] { c1.Length, r2.Length, c2.Length, ids.Length, scores.Length }.Any(n => n != r1.Length)) throw new InvalidDataException("检测框输出维度不一致。");
        var result = new List<DetectedObject>();
        for (var i = 0; i < r1.Length; i++)
        {
            var values = new[] { r1[i].D, c1[i].D, r2[i].D, c2[i].D, scores[i].D };
            var index = Array.IndexOf(package.ClassIds, ids[i].I);
            if (values.Any(v => !double.IsFinite(v)) || index < 0 || scores[i].D < 0 || scores[i].D > 1 || r2[i].D <= r1[i].D || c2[i].D <= c1[i].D)
                throw new InvalidDataException("检测输出包含非法框、类别或置信度。");
            // Inverse of HALCON 24.11 preprocess_dl_model_bbox_rect1: (coord + .5) * scale - .5.
            double Row(double v) => Math.Clamp((v + .5) * roi.Crop.Height / height - .5 + roi.Crop.RowBegin, roi.Crop.RowBegin - .5, roi.Crop.RowEnd + .5);
            double Col(double v) => Math.Clamp((v + .5) * roi.Crop.Width / width - .5 + roi.Crop.ColumnBegin, roi.Crop.ColumnBegin - .5, roi.Crop.ColumnEnd + .5);
            var bounds = new Rectangle2D(Row(r1[i].D), Col(c1[i].D), Row(r2[i].D), Col(c2[i].D));
            if (bounds.RowEnd <= bounds.RowBegin || bounds.ColumnEnd <= bounds.ColumnBegin) continue;
            result.Add(new(roi.Id, roi.Name, ids[i].I, package.ClassNames[index], scores[i].D, bounds));
        }
        return result.OrderByDescending(item => item.Confidence).ToArray();
    }
}
