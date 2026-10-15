using HalconDotNet;

namespace HalconVision.Halcon;

internal static class DlPreprocessing
{
    private static readonly object ProcedureGate = new();
    public static void Apply(HDict sample, HDict preprocess, CancellationToken cancellation)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT")
            ?? throw new InvalidOperationException("HALCONROOT 未设置，无法加载官方 DL 预处理过程。"), "procedures", "dl");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"官方 DL 预处理目录不存在：{directory}");
        lock (ProcedureGate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var engine = new HDevEngine(); engine.SetProcedurePath(directory);
            using var procedure = new HDevProcedure("preprocess_dl_samples");
            using var call = new HDevProcedureCall(procedure);
            using var sampleTuple = new HTuple(sample); using var preprocessTuple = new HTuple(preprocess);
            call.SetInputCtrlParamTuple("DLSampleBatch", sampleTuple);
            call.SetInputCtrlParamTuple("DLPreprocessParam", preprocessTuple); call.Execute();
        }
    }
}
