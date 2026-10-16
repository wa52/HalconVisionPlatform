using System.Diagnostics;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconClassificationExecutor(DlModelSessionCache? cache = null) : INodeExecutor
{
    public string TypeId => HalconNodeProvider.Classification;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var cancellation = context.CancellationToken; cancellation.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage source) throw new InvalidDataException("分类检测需要连接 Image 输入。");
        var path = context.Node.Parameters.TryGetValue("modelPackage", out var value) ? value.GetString() ?? "" : "";
        var threshold = context.Node.Parameters.TryGetValue("minimumConfidence", out var number) ? number.GetDouble() : .5;
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new InvalidDataException("最低置信度必须在 0–1 之间。");
        using var first = source.SelectObj(1); first.GetImageSize(out int width, out int height);
        using var channels = first.CountChannels();
        if (channels.I is not (1 or 3)) throw new InvalidDataException("分类预览仅支持灰度或 RGB 图像。");
        var rois = DlRoiSet.Read(context.Node, width, height);
        var clock = Stopwatch.StartNew();
        var results = new List<RoiClassificationResult>();
        HImage? preview = null;
        var transferred = false;
        try
        {
            NodeExecutionOutput output;
            using (var lease = DlSessionLease.Open(cache, path, context, "classification", "classification"))
            {
                var package = lease.Package; var session = lease.Session;
                context.Log($"分类模型已加载，按顺序处理 {rois.Count} 个 ROI。");
                foreach (var roi in rois)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var crop = roi.Crop.Crop(first);
                        using var prediction = session.Predict(crop, cancellation, out var milliseconds);
                        var scores = Decode(prediction, package);
                        var top = scores[0];
                        var status = new ClassificationStatus(top.Confidence >= threshold ? ClassificationStatus.Classified : ClassificationStatus.LowConfidence,
                            $"{roi.Name}：{top.ClassName}，置信度 {top.Confidence:P2}" + (top.Confidence < threshold ? "，低于阈值。" : "。"));
                        results.Add(new(roi.Id, roi.Name, roi.Crop.Bounds, top.ClassId, top.ClassName, top.Confidence, scores, status, milliseconds));
                        context.Log($"{status.Message} 推理 {milliseconds:F1} ms。");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { throw new InvalidOperationException($"ROI“{roi.Name}”分类失败：{ex.Message}", ex); }
                }
                preview = channels.I == 1 ? first.Compose3(first, first) : first.CopyImage();
                using var yellow = new HTuple(new int[] { 255, 220, 0 });
                foreach (var roi in rois.Where(item => item.Crop.IsExplicit))
                {
                    using var region = roi.Crop.Region();
                    var marked = preview.PaintRegion(region, yellow, "margin"); preview.Dispose(); preview = marked;
                }
                var single = results.Count == 1 ? results[0] : null;
                var lowCount = results.Count(item => item.Status.Code == ClassificationStatus.LowConfidence);
                var summary = single?.Status ?? new ClassificationStatus(lowCount == 0 ? ClassificationStatus.Classified : ClassificationStatus.LowConfidence,
                    $"完成 {results.Count} 个 ROI 分类，低置信度 {lowCount} 个；各区域结果见图像标注和 ROI 明细。");
                var ports = new Dictionary<string, JsonElement>
                {
                    ["preview"] = JsonSerializer.SerializeToElement(new { width, height, channels = 3 }),
                    ["classId"] = JsonSerializer.SerializeToElement(single?.ClassId), ["className"] = JsonSerializer.SerializeToElement(single?.ClassName),
                    ["confidence"] = JsonSerializer.SerializeToElement(single?.Confidence), ["scores"] = JsonSerializer.SerializeToElement(single?.Scores ?? []),
                    ["status"] = JsonSerializer.SerializeToElement(summary), ["roiResults"] = JsonSerializer.SerializeToElement(results),
                    ["roi"] = JsonSerializer.SerializeToElement(single?.Bounds),
                    ["modelInfo"] = JsonSerializer.SerializeToElement(new { directory = package.DirectoryPath, device = session.Runtime,
                        deviceIndex = session.DeviceIndex, roiCount = results.Count, cacheHit = lease.CacheHit, modelLoadMs = lease.LoadMilliseconds,
                        inferenceMs = results.Sum(item => item.InferenceMilliseconds), elapsedMs = clock.Elapsed.TotalMilliseconds })
                };
                var labels = results.Select(item => new RoiDisplayResult(item.RoiId, item.RoiName, item.Bounds,
                    $"{item.ClassName} · {item.Confidence:P1}", item.Status.Code == ClassificationStatus.LowConfidence)).ToArray();
                output = new(ports, new Dictionary<string, object?> { ["preview"] = preview, ["roiOverlay"] = new MultiRoiOverlay(labels) });
                cancellation.ThrowIfCancellationRequested(); lease.Complete();
            }
            cancellation.ThrowIfCancellationRequested();
            transferred = true;
            return ValueTask.FromResult(output);
        }
        finally { if (!transferred) preview?.Dispose(); }
    }
    private static ClassificationScore[] Decode(HDict prediction, DlModelPackage package)
    {
        using var ids = prediction.GetDictTuple("classification_class_ids");
        using var names = prediction.GetDictTuple("classification_class_names");
        using var confidence = prediction.GetDictTuple("classification_confidences");
        if (ids.Length == 0 || ids.Length != names.Length || ids.Length != confidence.Length) throw new InvalidDataException("分类输出维度无效。");
        var scores = new List<ClassificationScore>();
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i].I; var p = confidence[i].D;
            if (id < 0 || id >= package.ClassNames.Length || names[i].S != package.ClassNames[id] || !double.IsFinite(p) || p < -1e-6 || p > 1 + 1e-6)
                throw new InvalidDataException("分类输出与模型类别映射不一致。");
            scores.Add(new(id, package.ClassNames[id], Math.Clamp(p, 0, 1)));
        }
        return scores.OrderByDescending(item => item.Confidence).ToArray();
    }
}
