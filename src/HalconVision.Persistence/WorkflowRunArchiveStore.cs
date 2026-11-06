using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HalconVision.Domain;

namespace HalconVision.Persistence;

public sealed record ArchivedWorkflowRun(int SchemaVersion, DateTime ArchivedAtUtc,
    Guid ProjectId, string ProjectName, FlowDocument Flow, Guid RunId,
    DateTime StartedAtUtc, DateTime EndedAtUtc, WorkflowRunStatus Status,
    IReadOnlyList<NodeExecutionResult> NodeResults, IReadOnlyList<string> Errors,
    IReadOnlyList<string> Logs, ProductInspectionResult? Inspection);

/// <summary>Versioned managed snapshots. Native runtime outputs are deliberately outside the contract.</summary>
public sealed class WorkflowRunArchiveStore
{
    public const int MaximumArchiveBytes = 64 * 1024 * 1024;
    private sealed record Envelope(int Version, string Sha256, string Data);
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string Save(string directory, Guid projectId, string projectName, FlowDocument flow, WorkflowExecutionRun run)
    {
        ArgumentNullException.ThrowIfNull(flow); ArgumentNullException.ThrowIfNull(run);
        var snapshot = new ArchivedWorkflowRun(1, DateTime.UtcNow, projectId, projectName, flow, run.Id,
            run.StartedAtUtc, run.EndedAtUtc, run.Status, run.NodeResults, run.Errors, run.Logs, run.Inspection);
        if (run.FlowId != flow.Id) throw new InvalidDataException("归档流程与执行结果不一致。");
        Validate(snapshot);
        var data = JsonSerializer.Serialize(snapshot, Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, hash, data), Options);
        if (bytes.Length > MaximumArchiveBytes) throw new InvalidDataException("归档超过 64 MB 限制。");
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{run.Id:N}.run.json");
        var temp = Path.Combine(root, $".{run.Id:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            // A run is immutable: duplicate IDs never replace a previous archive.
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }

    public ArchivedWorkflowRun Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumArchiveBytes) throw new InvalidDataException("归档超过 64 MB 限制。");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        Envelope envelope;
        ArchivedWorkflowRun snapshot;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new InvalidDataException("归档内容为空。");
            if (envelope.Version != 1 || envelope.Data is null || string.IsNullOrEmpty(envelope.Sha256))
                throw new InvalidDataException("归档版本或完整性信息无效。");
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Data)));
            if (!string.Equals(actual, envelope.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("归档完整性校验失败。");
            snapshot = JsonSerializer.Deserialize<ArchivedWorkflowRun>(envelope.Data, Options) ?? throw new InvalidDataException("归档数据为空。");
        }
        catch (JsonException error) { throw new InvalidDataException("归档格式损坏。", error); }
        Validate(snapshot); return snapshot;
    }

    private static void Validate(ArchivedWorkflowRun snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.ProjectId == Guid.Empty || snapshot.RunId == Guid.Empty ||
            snapshot.Flow is null || snapshot.Flow.Id == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.ProjectName) ||
            snapshot.NodeResults is null || snapshot.Errors is null || snapshot.Logs is null ||
            snapshot.Flow.Nodes is null || snapshot.EndedAtUtc < snapshot.StartedAtUtc || !Enum.IsDefined(snapshot.Status))
            throw new InvalidDataException("归档身份、版本或执行信息无效。");
        var nodeIds = snapshot.Flow.Nodes.Select(n => n.Id).ToHashSet();
        if (nodeIds.Count != snapshot.Flow.Nodes.Count || snapshot.NodeResults.Select(n => n.NodeId).Distinct().Count() != snapshot.NodeResults.Count ||
            snapshot.NodeResults.Any(n => !nodeIds.Contains(n.NodeId) || !Enum.IsDefined(n.Status) || n.Status is NodeExecutionStatus.Running or NodeExecutionStatus.Idle ||
                !double.IsFinite(n.ElapsedMilliseconds) || n.ElapsedMilliseconds < 0 || n.Outputs is null))
            throw new InvalidDataException("归档节点结果与流程不一致或执行尚未完成。");
        if (snapshot.Inspection is { } inspection && (inspection.RunId != snapshot.RunId || inspection.FlowId != snapshot.Flow.Id))
            throw new InvalidDataException("归档产品判定与本轮身份不一致。");
    }
}
