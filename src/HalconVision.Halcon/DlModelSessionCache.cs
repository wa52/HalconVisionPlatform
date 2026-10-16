using System.Diagnostics;
using System.Text.Json;
using HalconVision.Application;

namespace HalconVision.Halcon;

/// <summary>Workspace-owned, bounded sessions. A lease holds exclusive access until node execution ends.</summary>
public sealed class DlModelSessionCache(int capacity = 2) : IDisposable
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;
    private long _clock;
    public int Count { get { _gate.Wait(); try { return _entries.Count; } finally { _gate.Release(); } } }

    internal DlSessionLease Acquire(string selected, NodeExecutionContext context, string task, string type)
    {
        _gate.Wait(context.CancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var identity = Identity(selected, context, task);
            var hit = _entries.TryGetValue(context.Node.Id, out var entry) && entry.Identity == identity;
            var timer = Stopwatch.StartNew();
            if (!hit)
            {
                Remove(context.Node.Id);
                while (_entries.Count >= _capacity) Remove(_entries.MinBy(pair => pair.Value.LastUse).Key);
                var package = DlModelPackage.Load(selected, context.CancellationToken, task);
                var session = new DlInferenceSession(package, context, type);
                try
                {
                    if (Identity(selected, context, task) != identity) throw new IOException("模型文件在加载期间发生变化，请选择固定版本目录。");
                    context.CancellationToken.ThrowIfCancellationRequested();
                    entry = new(identity, package, session, ++_clock);
                    _entries.Add(context.Node.Id, entry);
                }
                catch { session.Dispose(); throw; }
            }
            entry!.LastUse = ++_clock;
            context.Log(hit ? "复用已加载模型会话。" : $"模型会话加载完成 {timer.Elapsed.TotalMilliseconds:F1} ms，首次实际推理进行预热。");
            return new(entry.Package, entry.Session, hit, timer.Elapsed.TotalMilliseconds, successful =>
            {
                try { if (!successful) Remove(context.Node.Id); }
                finally { _gate.Release(); }
            });
        }
        catch { _gate.Release(); throw; }
    }

    public void Clear()
    {
        _gate.Wait();
        try { ClearCore(); }
        finally { _gate.Release(); }
    }
    private void Remove(Guid id)
    {
        if (_entries.Remove(id, out var entry)) entry.Session.Dispose();
    }
    private void ClearCore()
    {
        var entries = _entries.Values.ToArray(); _entries.Clear();
        var errors = new List<Exception>();
        foreach (var entry in entries) try { entry.Session.Dispose(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("释放缓存模型失败。", errors);
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; ClearCore(); }
        finally { _gate.Release(); }
    }
    private static string Identity(string selected, NodeExecutionContext context, string task)
    {
        if (string.IsNullOrWhiteSpace(selected)) throw new InvalidDataException("请先选择训练模型目录。");
        var path = Path.GetFullPath(selected);
        if (!Directory.Exists(path))
        {
            if (!File.Exists(path) || Path.GetFileName(path) is not ("manifest.json" or "model.hdl")) throw new FileNotFoundException("模型目录或入口文件不存在。", path);
            path = Path.GetDirectoryName(path)!;
        }
        var files = new[] { "model.hdl", "preprocess.hdict", "manifest.json", "classes.json", "checksums.json" }
            .Select(name => new FileInfo(Path.Combine(path, name)))
            .Select(file => file.Exists ? $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : $"{file.Name}:missing");
        return string.Join("|", path.ToUpperInvariant(), task,
            context.Node.Parameters.GetValueOrDefault("deviceRuntime").ValueKind == JsonValueKind.Undefined ? "gpu" : context.Node.Parameters["deviceRuntime"].GetRawText(),
            context.Node.Parameters.GetValueOrDefault("deviceIndex").ValueKind == JsonValueKind.Undefined ? "0" : context.Node.Parameters["deviceIndex"].GetRawText(), string.Join(";", files));
    }
    private sealed record Entry(string Identity, DlModelPackage Package, DlInferenceSession Session, long InitialUse)
    { public long LastUse { get; set; } = InitialUse; }
}

internal sealed class DlSessionLease(DlModelPackage package, DlInferenceSession session, bool cacheHit, double loadMilliseconds, Action<bool> release) : IDisposable
{
    private Action<bool>? _release = release;
    private bool _successful;
    public DlModelPackage Package { get; } = package;
    public DlInferenceSession Session { get; } = session;
    public bool CacheHit { get; } = cacheHit;
    public double LoadMilliseconds { get; } = loadMilliseconds;
    public void Complete() => _successful = true;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(_successful);
    public static DlSessionLease Open(DlModelSessionCache? cache, string selected, NodeExecutionContext context, string task, string type)
    {
        if (cache is not null) return cache.Acquire(selected, context, task, type);
        var timer = Stopwatch.StartNew();
        var package = DlModelPackage.Load(selected, context.CancellationToken, task);
        var session = new DlInferenceSession(package, context, type);
        return new(package, session, false, timer.Elapsed.TotalMilliseconds, _ => session.Dispose());
    }
}
