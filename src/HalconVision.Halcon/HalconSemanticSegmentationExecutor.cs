using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

/// <summary>Training-platform-compatible inference; native outputs transfer to the current execution run.</summary>
public sealed class HalconSemanticSegmentationExecutor : INodeExecutor
{
    private static readonly object ProcedureGate = new();
    public string TypeId => HalconNodeProvider.SemanticSegmentation;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        var cancellation = context.CancellationToken;
        cancellation.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage source)
            throw new InvalidDataException("语义分割检测需要连接读取图像或相机采集的 Image 输出。");
        var package = SemanticModelPackage.Load(Text(context.Node, "modelPackage", ""), cancellation);
        var runtime = Text(context.Node, "deviceRuntime", "gpu");
        if (runtime is not ("gpu" or "cpu")) throw new InvalidDataException("推理设备必须为 GPU 或 CPU。");
        var deviceIndexValue = Number(context.Node, "deviceIndex", 0);
        if (deviceIndexValue < 0 || deviceIndexValue > int.MaxValue || deviceIndexValue != Math.Truncate(deviceIndexValue))
            throw new InvalidDataException("设备索引必须为非负整数。");
        var minimum = Number(context.Node, "minimumArea", 1);
        var maximum = Number(context.Node, "maximumArea", 1_000_000_000);
        if (minimum < 1 || maximum < minimum) throw new InvalidDataException("面积范围无效，最小面积必须至少为 1 像素。");
        var background = ParseIds(Text(context.Node, "backgroundClassIds", "0"));
        var requested = ParseIds(Text(context.Node, "targetClassIds", ""));
        if (background.Concat(requested).Any(id => !package.ClassIds.Contains(id)))
            throw new InvalidDataException("背景/目标类别 ID 不在模型类别列表中。");
        var targets = requested.Count == 0 ? package.ClassIds.Where(id => !background.Contains(id)).ToHashSet() : requested;
        if (targets.Count == 0) throw new InvalidDataException("没有可检测的目标类别，请检查背景和目标类别配置。");
        var procedureDirectory = Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT")
            ?? throw new InvalidOperationException("HALCONROOT 未设置，无法加载官方 DL 预处理过程。"), "procedures", "dl");
        if (!Directory.Exists(procedureDirectory)) throw new DirectoryNotFoundException($"官方 DL 预处理目录不存在：{procedureDirectory}");
        using var first = source.SelectObj(1);
        first.GetImageSize(out int width, out int height);
        using var channels = first.CountChannels();
        if (channels.I is not (1 or 3)) throw new InvalidDataException("当前语义分割预览仅支持灰度或 RGB 图像。");
        var clock = Stopwatch.StartNew();
        HDlModel? model = null;
        HDlDevice[] devices = [];
        HImage? mask = null, preview = null;
        HRegion? regions = null;
        NodeExecutionOutput? output = null;
        var succeeded = false;
        try
        {
            context.Log($"语义分割：加载模型 {package.DirectoryPath}，设备 {runtime.ToUpperInvariant()}[{deviceIndexValue}]。");
            cancellation.ThrowIfCancellationRequested();
            model = new HDlModel(package.ModelPath);
            using var type = model.GetDlModelParam("type");
            using var ids = model.GetDlModelParam("class_ids");
            if (type.S != "segmentation") throw new InvalidDataException("model.hdl 不是语义分割模型。");
            if (!ids.ToIArr().Order().SequenceEqual(package.ClassIds.Order()))
                throw new InvalidDataException("实际模型 class_ids 与模型目录的类别定义不一致。");
            using var empty = new HTuple();
            using var preprocess = new HDict(package.PreprocessPath, empty, empty);
            using var domain = preprocess.GetDictTuple("domain_handling");
            if (domain.S != "full_domain") throw new InvalidDataException("只支持训练平台 full_domain 预处理；不支持猜测裁剪或填充坐标。");
            devices = HDlDevice.QueryAvailableDlDevices("runtime", runtime);
            var deviceIndex = (int)deviceIndexValue;
            if (deviceIndex >= devices.Length) throw new InvalidOperationException($"HALCON 可用 {runtime.ToUpperInvariant()} 设备数为 {devices.Length}，没有索引 {deviceIndex}。");
            using var device = new HTuple(devices[deviceIndex]);
            model.SetDlModelParam("device", device);
            model.SetDlModelParam("batch_size", 1);
            using var sample = new HDict();
            sample.SetDictObject(first, "image");
            cancellation.ThrowIfCancellationRequested();
            lock (ProcedureGate)
            {
                cancellation.ThrowIfCancellationRequested();
                using var engine = new HDevEngine();
                engine.SetProcedurePath(procedureDirectory);
                using var procedure = new HDevProcedure("preprocess_dl_samples");
                using var call = new HDevProcedureCall(procedure);
                using var sampleTuple = new HTuple(sample);
                using var preprocessTuple = new HTuple(preprocess);
                call.SetInputCtrlParamTuple("DLSampleBatch", sampleTuple);
                call.SetInputCtrlParamTuple("DLPreprocessParam", preprocessTuple);
                call.Execute();
            }
            cancellation.ThrowIfCancellationRequested();
            var inference = Stopwatch.StartNew();
            var predictions = model.ApplyDlModel([sample], empty);
            try
            {
                inference.Stop();
                cancellation.ThrowIfCancellationRequested();
                if (predictions.Length != 1) throw new InvalidDataException("HALCON 未返回唯一单帧分割结果。");
                using var labelObject = predictions[0].GetDictObject("segmentation_image");
                using var labels = new HImage(labelObject);
                mask = labels.ZoomImageSize(width, height, "nearest_neighbor");
                regions = new HRegion(); regions.GenEmptyObj();
                var objects = new List<SegmentationObject>();
                var classAreas = new List<SegmentationClassArea>();
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
                            new(row, column), new(row1, col1, row2, col2)));
                    }
                    var joined = regions.ConcatObj(filtered); regions.Dispose(); regions = joined;
                }
                using var rgb = channels.I == 1 ? first.Compose3(first, first) : first.CopyImage();
                using var green = new HTuple(new int[] { 0, 255, 0 });
                preview = objects.Count > 0 ? rgb.PaintRegion(regions, green, "margin") : rgb.CopyImage();
                var count = objects.Count;
                var totalArea = objects.Sum(item => item.Area);
                var status = new SegmentationStatus(count > 0 ? SegmentationStatus.Detected : SegmentationStatus.Empty,
                    count > 0 ? $"检出 {count} 个目标区域，面积 {totalArea:F0} 像素。" : "未检出满足类别和面积条件的区域。");
                var serialized = new Dictionary<string, JsonElement>
                {
                    ["preview"] = JsonSerializer.SerializeToElement(new { width, height, channels = 3 }),
                    ["mask"] = JsonSerializer.SerializeToElement(new { width, height, channels = 1 }),
                    ["regions"] = JsonSerializer.SerializeToElement(new { width, height, count }),
                    ["count"] = JsonSerializer.SerializeToElement(count), ["area"] = JsonSerializer.SerializeToElement(totalArea),
                    ["objects"] = JsonSerializer.SerializeToElement(objects), ["classes"] = JsonSerializer.SerializeToElement(classAreas),
                    ["status"] = JsonSerializer.SerializeToElement(status),
                    ["modelInfo"] = JsonSerializer.SerializeToElement(new { directory = package.DirectoryPath,
                        device = runtime, deviceIndex, inferenceMs = inference.Elapsed.TotalMilliseconds, elapsedMs = clock.Elapsed.TotalMilliseconds })
                };
                cancellation.ThrowIfCancellationRequested();
                var native = new Dictionary<string, object?> { ["preview"] = preview, ["mask"] = mask, ["regions"] = regions };
                context.Log($"语义分割完成：{status.Code}，{count} 个区域，推理 {inference.Elapsed.TotalMilliseconds:F1} ms。");
                output = new NodeExecutionOutput(serialized, native);
            }
            finally { foreach (var prediction in predictions) prediction.Dispose(); }
            cancellation.ThrowIfCancellationRequested();
            succeeded = true;
        }
        catch (HalconException ex)
        {
            throw new InvalidOperationException($"HALCON 语义分割失败（模型：{package.DirectoryPath}，设备：{runtime}）。原始错误：{ex.Message}", ex);
        }
        finally
        {
            var cleanupSucceeded = false;
            try
            {
                try { model?.ClearDlModel(); }
                finally
                {
                    model?.Dispose();
                    foreach (var device in devices) device.Dispose();
                }
                cleanupSucceeded = true;
            }
            finally
            {
                if (!succeeded || !cleanupSucceeded)
                { preview?.Dispose(); mask?.Dispose(); regions?.Dispose(); }
            }
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
