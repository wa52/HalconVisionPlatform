using System.Diagnostics;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;

namespace HalconVision.Halcon;

/// <summary>One model per node invocation, reused by its ROIs and disposed before outputs transfer to the run.</summary>
internal sealed class DlInferenceSession : IDisposable
{
    private HDlModel? _model;
    private HDict? _preprocess;
    private HDlDevice[] _devices = [];
    public string Runtime { get; }
    public int DeviceIndex { get; }
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
                if (!ids.ToIArr().Order().SequenceEqual(package.ClassIds.Order())) throw new InvalidDataException("分割模型类别与 manifest.json 不一致。");
            }
            using var empty = new HTuple();
            _preprocess = new HDict(package.PreprocessPath, empty, empty);
            using var domain = _preprocess.GetDictTuple("domain_handling");
            if (domain.S != "full_domain") throw new InvalidDataException("当前仅支持训练平台 full_domain 预处理。");
            _devices = HDlDevice.QueryAvailableDlDevices("runtime", Runtime);
            if (DeviceIndex >= _devices.Length) throw new InvalidOperationException($"未发现 {Runtime} 设备索引 {DeviceIndex}，可用数 {_devices.Length}。");
            using var device = new HTuple(_devices[DeviceIndex]);
            _model.SetDlModelParam("device", device); _model.SetDlModelParam("batch_size", 1);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("模型初始化失败，清理资源时也发生错误。", error, cleanup); }
            throw;
        }
    }
    public HDict Predict(HImage image, CancellationToken cancellation, out double inferenceMilliseconds)
    {
        cancellation.ThrowIfCancellationRequested();
        using var sample = new HDict(); sample.SetDictObject(image, "image");
        DlPreprocessing.Apply(sample, _preprocess!, cancellation);
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
        var model = _model; var preprocess = _preprocess; var devices = _devices;
        _model = null; _preprocess = null; _devices = [];
        var errors = new List<Exception>();
        if (model is not null)
        {
            Attempt(model.ClearDlModel);
            Attempt(model.Dispose);
        }
        if (preprocess is not null) Attempt(preprocess.Dispose);
        foreach (var device in devices) Attempt(device.Dispose);
        if (errors.Count > 0) throw new AggregateException("释放深度学习资源失败。", errors);
        void Attempt(Action action) { try { action(); } catch (Exception ex) { errors.Add(ex); } }
    }
}
