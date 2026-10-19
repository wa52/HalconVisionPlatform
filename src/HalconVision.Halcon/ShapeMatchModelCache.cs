using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;

namespace HalconVision.Halcon;

/// <summary>Workspace-owned, bounded shape models. A lease serializes model access and clearing.</summary>
public sealed class ShapeMatchModelCache(int capacity = 4) : IDisposable
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;
    private long _clock;
    public int Count { get { _gate.Wait(); try { return _entries.Count; } finally { _gate.Release(); } } }
    public long ModelLoads { get; private set; }

    internal Lease Acquire(NodeExecutionContext context)
    {
        _gate.Wait(context.CancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = QuickMatchParameters.TemplatePath(context.Node);
            var identity = Fingerprint(path, context);
            var hit = _entries.TryGetValue(context.Node.Id, out var entry) && entry.Identity == identity;
            if (!hit)
            {
                Remove(context.Node.Id);
                while (_entries.Count >= _capacity) Remove(_entries.MinBy(pair => pair.Value.LastUse).Key);
                context.Log("正在创建 HALCON 形状模板模型。");
                using var image = new HImage(path);
                using var gray = QuickMatchParameters.Gray(image);
                gray.GetImageSize(out int width, out int height);
                var roiNode = context.Node with { Parameters = new(context.Node.Parameters)
                    { ["roi"] = context.Node.Parameters.GetValueOrDefault("templateRoi", JsonSerializer.SerializeToElement("")) } };
                var roi = DlCropRoi.Read(roiNode, width, height);
                using var region = roi.Region(); using var template = gray.ReduceDomain(region);
                HShapeModel? model = new(); HXLDCont? contours = null;
                try
                {
                    using var auto = new HTuple("auto");
                    using var contrast = new HTuple(QuickMatchParameters.Integer(context.Node, "contrast", 30, 1, 255));
                    using var minimumContrast = new HTuple(QuickMatchParameters.Integer(context.Node, "minimumContrast", 10, 1, 255));
                    model.CreateShapeModel(template, auto, -Math.PI, 2 * Math.PI, auto, auto,
                        QuickMatchParameters.Boolean(context.Node, "ignorePolarity", false) ? "ignore_global_polarity" : "use_polarity",
                        contrast, minimumContrast);
                    contours = model.GetShapeModelContours(1);
                    if (contours.CountObj() == 0) throw new InvalidDataException("模板没有足够的有效边缘，请重新选择模板范围或降低对比度。");
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (Fingerprint(path, context) != identity) throw new IOException("模板文件在建模期间发生变化，请重新运行。");
                    entry = new(identity, model, contours, ++_clock);
                    _entries.Add(context.Node.Id, entry); ModelLoads++;
                    model = null; contours = null;
                }
                finally { contours?.Dispose(); model?.Dispose(); }
            }
            entry!.LastUse = ++_clock;
            return new(entry.Model, entry.Contours, hit, successful =>
            {
                try { if (!successful) Remove(context.Node.Id); }
                finally { _gate.Release(); }
            });
        }
        catch { _gate.Release(); throw; }
    }

    private static string Fingerprint(string path, NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var settings = new[] { "templateRoi", "contrast", "minimumContrast", "ignorePolarity" }
            .Select(key => context.Node.Parameters.TryGetValue(key, out var value) ? value.GetRawText() : "default");
        return path.ToUpperInvariant() + "|" + hash + "|" + string.Join("|", settings);
    }
    private void Remove(Guid id)
    {
        if (!_entries.Remove(id, out var entry)) return;
        try { entry.Contours.Dispose(); } finally { entry.Model.Dispose(); }
    }
    public void Clear()
    {
        _gate.Wait();
        try { ClearCore(); }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; ClearCore(); }
        finally { _gate.Release(); }
    }
    private void ClearCore()
    {
        var errors = new List<Exception>();
        foreach (var id in _entries.Keys.ToArray())
            try { Remove(id); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("释放匹配模型失败。", errors);
    }
    private sealed record Entry(string Identity, HShapeModel Model, HXLDCont Contours, long InitialUse)
    { public long LastUse { get; set; } = InitialUse; }

    internal sealed class Lease(HShapeModel model, HXLDCont contours, bool cacheHit, Action<bool> release) : IDisposable
    {
        private Action<bool>? _release = release;
        private bool _successful;
        public HShapeModel Model { get; } = model;
        public HXLDCont Contours { get; } = contours;
        public bool CacheHit { get; } = cacheHit;
        public void Complete() => _successful = true;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(_successful);
    }
}
