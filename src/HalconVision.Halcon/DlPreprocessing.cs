using HalconDotNet;

namespace HalconVision.Halcon;

/// <summary>Model-session-owned official procedure. Each invocation owns its call and frame arguments.</summary>
internal sealed class DlPreprocessing : IDisposable
{
    private static readonly object ProcedureGate = new();
    private HDevEngine? _engine;
    private HDevProcedure? _procedure;
    private bool _disposed;

    public DlPreprocessing()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT")
            ?? throw new InvalidOperationException("HALCONROOT 未设置，无法加载官方 DL 预处理过程。"), "procedures", "dl");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"官方 DL 预处理目录不存在：{directory}");
        lock (ProcedureGate)
        {
            try
            {
                _engine = new HDevEngine(); _engine.SetProcedurePath(directory);
                _procedure = new HDevProcedure("preprocess_dl_samples");
            }
            catch (Exception error)
            {
                try { Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("加载官方预处理过程失败，清理资源也发生错误。", error, cleanup); }
                throw;
            }
        }
    }

    public void Apply(HDict sample, HDict preprocess, CancellationToken cancellation)
    {
        lock (ProcedureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
            using var call = new HDevProcedureCall(_procedure!);
            using var sampleTuple = new HTuple(sample); using var preprocessTuple = new HTuple(preprocess);
            call.SetInputCtrlParamTuple("DLSampleBatch", sampleTuple);
            call.SetInputCtrlParamTuple("DLPreprocessParam", preprocessTuple); call.Execute();
            cancellation.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        lock (ProcedureGate)
        {
            if (_disposed) return;
            _disposed = true;
            var procedure = _procedure; var engine = _engine; _procedure = null; _engine = null;
            var errors = new List<Exception>();
            try { procedure?.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            try { engine?.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("释放官方预处理过程失败。", errors);
        }
    }
}
