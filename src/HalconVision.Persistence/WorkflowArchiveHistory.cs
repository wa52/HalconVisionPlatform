namespace HalconVision.Persistence;

public sealed record ArchiveHistoryEntry(string File, ArchivedWorkflowRun? Run, string Error)
{
    public string Project => Run?.ProjectName ?? "损坏或不可读取";
    public string Flow => Run?.Flow.Name ?? "—";
    public string Verdict => Run is null ? "读取错误" : Run.Status == HalconVision.Domain.WorkflowRunStatus.Failed ? "执行失败" : Run.Status == HalconVision.Domain.WorkflowRunStatus.Cancelled ? "已取消" : Run.Inspection?.Label ?? "执行完成";
    public string Completed => Run?.EndedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string RunId => Run?.RunId.ToString() ?? "—";
}
public sealed record ArchiveHistoryPage(IReadOnlyList<ArchiveHistoryEntry> Entries, bool HasNext);
public static class WorkflowArchiveHistory
{
    public const int PageSize = 50;
    public static ArchiveHistoryPage ReadPage(string directory, int page, CancellationToken cancellation = default)
    {
        if (page < 0 || page > 1000000) throw new ArgumentOutOfRangeException(nameof(page));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("归档目录不存在，请选择已有目录或先运行生成归档。");
        var files = Directory.EnumerateFiles(directory, "*.run.json", SearchOption.TopDirectoryOnly)
            .Select(p => { cancellation.ThrowIfCancellationRequested(); return new FileInfo(p); }).OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
            .Skip(checked(page * PageSize)).Take(PageSize + 1).ToArray();
        var store = new WorkflowRunArchiveStore(); var entries = new List<ArchiveHistoryEntry>();
        long readBytes = 0;
        foreach (var file in files.Take(PageSize))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (file.Length > 8 * 1024 * 1024 || readBytes + file.Length > 32 * 1024 * 1024)
                { entries.Add(new(file.FullName, null, "超过历史页读取预算（单条8MB/本页32MB）；完整文件仍保留，可用归档读取API检查。")); continue; }
                readBytes += file.Length;
                entries.Add(new(file.FullName, store.Read(file.FullName), ""));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
            { entries.Add(new(file.FullName, null, error.Message)); }
        }
        return new(entries, files.Length > PageSize);
    }
}
