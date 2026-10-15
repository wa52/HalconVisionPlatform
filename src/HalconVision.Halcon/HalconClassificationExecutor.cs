using System.Diagnostics;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconClassificationExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.Classification;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var cancellation = context.CancellationToken; cancellation.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage source)
            throw new InvalidDataException("分类检测需要连接 Image 输入。");
        var package = DlModelPackage.Load(Text(context.Node, "modelPackage", ""), cancellation, "classification");
        if (!package.ClassIds.SequenceEqual(Enumerable.Range(0, package.ClassIds.Length)))
            throw new InvalidDataException("训练平台分类模型的 classIds 必须为从 0 开始的连续索引。");
        var runtime = Text(context.Node, "deviceRuntime", "gpu");
        if (runtime is not ("gpu" or "cpu")) throw new InvalidDataException("推理设备必须为 GPU 或 CPU。");
        var indexValue = Number(context.Node, "deviceIndex", 0);
        var threshold = Number(context.Node, "minimumConfidence", 0.5);
        if (indexValue < 0 || indexValue > int.MaxValue || indexValue != Math.Truncate(indexValue))
            throw new InvalidDataException("设备索引必须为非负整数。");
        if (threshold is < 0 or > 1) throw new InvalidDataException("最低置信度必须在 0–1 之间。");
        using var first = source.SelectObj(1);
        first.GetImageSize(out int width, out int height);
        using var channels = first.CountChannels();
        if (channels.I is not (1 or 3)) throw new InvalidDataException("当前分类预览仅支持灰度或 RGB 图像。");
        var roi = DlCropRoi.Read(context.Node, width, height);
        using var crop = roi.Crop(first);
        var timer = Stopwatch.StartNew();
        HDlModel? model = null;
        HDlDevice[] devices = [];
        HImage? preview = null;
        NodeExecutionOutput? output = null;
        var succeeded = false;
        try
        {
            context.Log($"分类检测：加载 {package.DirectoryPath}，裁剪范围 {roi.Width}×{roi.Height}，设备 {runtime}[{indexValue}]。");
            model = new HDlModel(package.ModelPath);
            using var type = model.GetDlModelParam("type");
            if (type.S != "classification") throw new InvalidDataException("model.hdl 不是单标签分类模型。");
            using var modelNames = model.GetDlModelParam("class_names");
            if (!modelNames.ToSArr().SequenceEqual(package.ClassNames)) throw new InvalidDataException("模型类别名称与 manifest.json 不一致。");
            using var empty = new HTuple();
            using var preprocess = new HDict(package.PreprocessPath, empty, empty);
            using var domain = preprocess.GetDictTuple("domain_handling");
            if (domain.S != "full_domain") throw new InvalidDataException("分类检测目前只支持 full_domain 预处理。");
            devices = HDlDevice.QueryAvailableDlDevices("runtime", runtime);
            var deviceIndex = (int)indexValue;
            if (deviceIndex >= devices.Length) throw new InvalidOperationException($"未发现 {runtime} 设备索引 {deviceIndex}，可用设备数：{devices.Length}。");
            using var device = new HTuple(devices[deviceIndex]);
            model.SetDlModelParam("device", device); model.SetDlModelParam("batch_size", 1);
            using var sample = new HDict(); sample.SetDictObject(crop, "image");
            DlPreprocessing.Apply(sample, preprocess, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var inference = Stopwatch.StartNew();
            var predictions = model.ApplyDlModel([sample], empty);
            try
            {
                inference.Stop(); cancellation.ThrowIfCancellationRequested();
                if (predictions.Length != 1) throw new InvalidDataException("HALCON 未返回唯一分类结果。");
                using var ids = predictions[0].GetDictTuple("classification_class_ids");
                using var names = predictions[0].GetDictTuple("classification_class_names");
                using var confidences = predictions[0].GetDictTuple("classification_confidences");
                if (ids.Length == 0 || ids.Length != names.Length || ids.Length != confidences.Length)
                    throw new InvalidDataException("HALCON 分类结果维度无效。");
                var scores = new List<ClassificationScore>();
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = ids[i].I; var confidence = confidences[i].D;
                    if (id < 0 || id >= package.ClassNames.Length || names[i].S != package.ClassNames[id] ||
                        !double.IsFinite(confidence) || confidence < -1e-6 || confidence > 1 + 1e-6)
                        throw new InvalidDataException("分类输出的 ID、名称或置信度与模型约定不一致。");
                    scores.Add(new(id, package.ClassNames[id], Math.Clamp(confidence, 0, 1)));
                }
                var ordered = scores.OrderByDescending(item => item.Confidence).ToArray();
                var top = ordered[0];
                var status = new ClassificationStatus(top.Confidence >= threshold ? ClassificationStatus.Classified : ClassificationStatus.LowConfidence,
                    $"类别：{top.ClassName}，置信度：{top.Confidence:P2}" + (top.Confidence < threshold ? "，低于设置阈值。" : "。"));
                using var rgb = channels.I == 1 ? first.Compose3(first, first) : first.CopyImage();
                using var region = roi.Region();
                using var yellow = new HTuple(new int[] { 255, 220, 0 });
                preview = roi.IsExplicit ? rgb.PaintRegion(region, yellow, "margin") : rgb.CopyImage();
                var ports = new Dictionary<string, JsonElement>
                {
                    ["preview"] = JsonSerializer.SerializeToElement(new { width, height, channels = 3 }),
                    ["classId"] = JsonSerializer.SerializeToElement(top.ClassId), ["className"] = JsonSerializer.SerializeToElement(top.ClassName),
                    ["confidence"] = JsonSerializer.SerializeToElement(top.Confidence), ["scores"] = JsonSerializer.SerializeToElement(ordered),
                    ["status"] = JsonSerializer.SerializeToElement(status), ["roi"] = JsonSerializer.SerializeToElement(roi),
                    ["modelInfo"] = JsonSerializer.SerializeToElement(new { directory = package.DirectoryPath, device = runtime,
                        deviceIndex, inferenceMs = inference.Elapsed.TotalMilliseconds, elapsedMs = timer.Elapsed.TotalMilliseconds })
                };
                var native = new Dictionary<string, object?> { ["preview"] = preview };
                if (roi.IsExplicit) native["roiOverlay"] = new ImageRoiOverlay(roi.Bounds);
                output = new(ports, native);
                context.Log($"分类完成：{status.Message} 推理 {inference.Elapsed.TotalMilliseconds:F1} ms。");
            }
            finally { foreach (var prediction in predictions) prediction.Dispose(); }
            cancellation.ThrowIfCancellationRequested(); succeeded = true;
        }
        catch (HalconException ex)
        {
            throw new InvalidOperationException($"HALCON 分类失败（模型：{package.DirectoryPath}，设备：{runtime}）。原始错误：{ex.Message}", ex);
        }
        finally
        {
            var cleaned = false;
            try
            {
                try { model?.ClearDlModel(); }
                finally { model?.Dispose(); foreach (var device in devices) device.Dispose(); }
                cleaned = true;
            }
            finally { if (!succeeded || !cleaned) preview?.Dispose(); }
        }
        return ValueTask.FromResult(output!);
    }
    private static string Text(NodeInstance node, string key, string fallback) =>
        node.Parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? fallback : fallback;
    private static double Number(NodeInstance node, string key, double fallback)
    {
        var value = node.Parameters.TryGetValue(key, out var json) ? json.GetDouble() : fallback;
        if (!double.IsFinite(value)) throw new InvalidDataException($"参数 {key} 必须为有限数值。");
        return value;
    }
}
