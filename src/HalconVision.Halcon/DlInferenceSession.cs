using System.Diagnostics;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;

namespace HalconVision.Halcon;

/// <summary>Model runtime protected by an exclusive cache lease. Executors reset mutable options per invocation; frames are not cached.</summary>
internal sealed class DlInferenceSession : IDisposable
{
    private HDlModel? _model;
    private HDict? _preprocess;
    private DlPreprocessing? _preprocessing;
    private HDlDevice[] _devices = [];
    public string Runtime { get; }
    public int DeviceIndex { get; }
    public int DetectionWidth { get; }
    public int DetectionHeight { get; }
    public DlInferenceSession(DlModelPackage package, NodeExecutionContext context, string modelType)
    {
        Runtime = context.Node.Parameters.TryGetValue("deviceRuntime", out var runtime) ? runtime.GetString() ?? "gpu" : "gpu";
        var number = context.Node.Parameters.TryGetValue("deviceIndex", out var index) ? index.GetDouble() : 0;
        if (Runtime is not ("gpu" or "cpu") || !double.IsFinite(number) || number < 0 || number > int.MaxValue || number != Math.Truncate(number))
            throw new InvalidDataException("推理设备或设备索引无效。");
        DeviceIndex = (int)number;
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            _model = new HDlModel(package.ModelPath);
            using var type = _model.GetDlModelParam("type");
            if (type.S != modelType) throw new InvalidDataException($"实际模型类型不是 {modelType}。");
            if (modelType == "classification")
            {
                using var names = _model.GetDlModelParam("class_names");
                if (!names.ToSArr().SequenceEqual(package.ClassNames) || !package.ClassIds.SequenceEqual(Enumerable.Range(0, package.ClassIds.Length)))
                    throw new InvalidDataException("分类模型的类别映射与 manifest.json 不一致。");
            }
            else
            {
                using var ids = _model.GetDlModelParam("class_ids");
                if (!ids.ToIArr().Order().SequenceEqual(package.ClassIds.Order())) throw new InvalidDataException("模型类别与 manifest.json 不一致。");
                if (modelType == "detection")
                {
                    using var names = _model.GetDlModelParam("class_names");
                    var actualIds = ids.ToIArr(); var actualNames = names.ToSArr();
                    if (actualNames.Length != actualIds.Length || actualIds.Select((id, i) =>
                        package.ClassIds.Contains(id) && package.ClassNames[Array.IndexOf(package.ClassIds, id)] == actualNames[i]).Any(matches => !matches))
                        throw new InvalidDataException("检测模型类别名称与 manifest.json 不一致。");
                    using var instance = _model.GetDlModelParam("instance_type");
                    if (instance.S != "rectangle1") throw new NotSupportedException("目标检测节点目前只支持 rectangle1 水平框。");
                    using var w = _model.GetDlModelParam("image_width"); using var h = _model.GetDlModelParam("image_height");
                    DetectionWidth = w.I; DetectionHeight = h.I;
                }
            }
            using var empty = new HTuple();
            _preprocess = new HDict(package.PreprocessPath, empty, empty);
            using var domain = _preprocess.GetDictTuple("domain_handling");
            if (domain.S != "full_domain") throw new InvalidDataException("当前仅支持训练平台 full_domain 预处理。");
            if (modelType == "detection")
            {
                using var w = _preprocess.GetDictTuple("image_width"); using var h = _preprocess.GetDictTuple("image_height");
                using var task = _preprocess.GetDictTuple("model_type");
                if (w.I != DetectionWidth || h.I != DetectionHeight || task.S != "detection" || DetectionWidth <= 0 || DetectionHeight <= 0)
                    throw new InvalidDataException("检测模型与训练预处理的任务或尺寸不一致。");
            }
            _devices = HDlDevice.QueryAvailableDlDevices("runtime", Runtime);
            if (DeviceIndex >= _devices.Length) throw new InvalidOperationException($"未发现 {Runtime} 设备索引 {DeviceIndex}，可用数 {_devices.Length}。");
            using var device = new HTuple(_devices[DeviceIndex]);
            _model.SetDlModelParam("device", device); _model.SetDlModelParam("batch_size", 1);
            _preprocessing = new DlPreprocessing();
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("模型初始化失败，清理资源时也发生错误。", error, cleanup); }
            throw;
        }
    }
    public void ConfigureDetection(double confidence, double overlap, int maximum)
    {
        _model!.SetDlModelParam("min_confidence", confidence);
        _model.SetDlModelParam("max_overlap", overlap);
        _model.SetDlModelParam("max_overlap_class_agnostic", 1d);
        _model.SetDlModelParam("max_num_detections", maximum);
    }
    public HDict Predict(HImage image, CancellationToken cancellation, out double inferenceMilliseconds)
    {
        cancellation.ThrowIfCancellationRequested();
        using var sample = new HDict(); sample.SetDictObject(image, "image");
        _preprocessing!.Apply(sample, _preprocess!, cancellation);
        cancellation.ThrowIfCancellationRequested();
        using var empty = new HTuple();
        var clock = Stopwatch.StartNew();
        var predictions = _model!.ApplyDlModel([sample], empty);
        inferenceMilliseconds = clock.Elapsed.TotalMilliseconds;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (predictions.Length != 1) throw new InvalidDataException("HALCON 未返回唯一 ROI 预测结果。");
            return predictions[0];
        }
        catch { foreach (var prediction in predictions) prediction.Dispose(); throw; }
    }
    public void Dispose()
    {
        var model = _model; var preprocess = _preprocess; var processing = _preprocessing; var devices = _devices;
        _model = null; _preprocess = null; _preprocessing = null; _devices = [];
        var errors = new List<Exception>();
        if (model is not null)
        {
            Attempt(model.ClearDlModel);
            Attempt(model.Dispose);
        }
        if (preprocess is not null) Attempt(preprocess.Dispose);
        if (processing is not null) Attempt(processing.Dispose);
        foreach (var device in devices) Attempt(device.Dispose);
        if (errors.Count > 0) throw new AggregateException("释放深度学习资源失败。", errors);
        void Attempt(Action action) { try { action(); } catch (Exception ex) { errors.Add(ex); } }
    }
}
