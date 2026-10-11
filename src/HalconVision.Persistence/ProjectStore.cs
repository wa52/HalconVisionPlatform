using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using HalconVision.Domain;

namespace HalconVision.Persistence;

public sealed record ProjectWorkspace(string RootPath, ProjectDocument Project, string ProjectFilePath = "");
public sealed record ProjectFileEntry(string RelativePath, bool IsDirectory, long Size);

public sealed class ProjectStore
{
    public const string ProjectFileName = "project.json";
    public const string ProjectExtension = ".hvproj";
    private const long MaxImportBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProjectWorkspace CreateProject(string projectFilePath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        ValidateSegment(name, "项目名称");
        var path = Path.GetFullPath(projectFilePath);
        if (!string.Equals(Path.GetExtension(path), ProjectExtension, StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ProjectExtension);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"项目文件已存在：{path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var flow = new FlowDocument { Name = "主流程" };
        var project = new ProjectDocument { Name = name, DefaultFlowId = flow.Id.ToString("N"), Flows = [flow] };
        var workspace = new ProjectWorkspace(Path.GetDirectoryName(path)!, project, path);
        SaveProject(workspace, [flow]);
        return workspace;
    }

    public ProjectWorkspace OpenProject(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        var path = Path.GetFullPath(projectFilePath);
        if (!File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ProjectExtension, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFileName(path), ProjectFileName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("请选择 HALCON 项目文件（.hvproj）。", path);
        var project = ReadJson<ProjectDocument>(path);
        if (project.SchemaVersion != 1 || project.Id == Guid.Empty || string.IsNullOrWhiteSpace(project.Name))
            throw new InvalidDataException("项目清单版本不受支持或内容不完整。");
        project = project with { Flows = project.Flows ?? [] };
        var root = Path.GetDirectoryName(path)!;
        if (string.Equals(Path.GetFileName(path), ProjectFileName, StringComparison.OrdinalIgnoreCase) && project.Flows.Count == 0)
        {
            var legacyFlowsPath = Resolve(root, "flows");
            if (Directory.Exists(legacyFlowsPath))
                project = project with { Flows = Directory.EnumerateFiles(legacyFlowsPath, "*.flow.json").Select(ReadJson<FlowDocument>).ToList() };
        }
        if (project.Flows.Count == 0) throw new InvalidDataException("项目文件中没有流程。请新建项目或选择有效项目文件。");
        return new ProjectWorkspace(root, project, path);
    }

    public ProjectDocument SaveProject(ProjectWorkspace workspace, IReadOnlyList<FlowDocument>? flows = null)
    {
        var updated = workspace.Project with { UpdatedAtUtc = DateTime.UtcNow, Flows = flows?.ToList() ?? workspace.Project.Flows };
        var path = string.IsNullOrWhiteSpace(workspace.ProjectFilePath)
            ? Resolve(workspace.RootPath, ProjectFileName)
            : Path.GetFullPath(workspace.ProjectFilePath);
        AtomicWrite(path, JsonSerializer.Serialize(updated, JsonOptions));
        return updated;
    }

    public IReadOnlyList<FlowDocument> ListFlows(ProjectWorkspace workspace) => workspace.Project.Flows
        .OrderBy(flow => flow.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public FlowDocument CreateFlow(ProjectWorkspace workspace, string name)
    {
        ValidateSegment(name, "流程名称");
        return new FlowDocument { Name = name };
    }

    public FlowDocument CopyFlow(ProjectWorkspace workspace, FlowDocument source, string name)
    {
        ValidateSegment(name, "流程名称");
        var copy = source with
        {
            Id = Guid.NewGuid(), Name = name,
            Nodes = source.Nodes.Select(node => node with { Id = Guid.NewGuid() }).ToList(),
            Edges = [], Variables = new Dictionary<string, System.Text.Json.JsonElement>(source.Variables),
            AssetReferences = [.. source.AssetReferences]
        };
        var idMap = source.Nodes.Zip(copy.Nodes).ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
        copy = copy with { Edges = source.Edges.Select(edge => edge with { Id = Guid.NewGuid(), FromNodeId = idMap[edge.FromNodeId], ToNodeId = idMap[edge.ToNodeId] }).ToList() };
        return copy;
    }

    public FlowDocument LoadFlow(ProjectWorkspace workspace, Guid flowId)
    {
        var path = FlowPath(workspace, flowId);
        var flow = ReadJson<FlowDocument>(path);
        if (flow.Id != flowId || flow.SchemaVersion != 1) throw new InvalidDataException("流程 ID 或 schema 版本无效。");
        return flow;
    }

    public void SaveFlow(ProjectWorkspace workspace, FlowDocument flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Id == Guid.Empty || flow.SchemaVersion != 1) throw new InvalidDataException("不能保存无效流程。");
        foreach (var reference in flow.AssetReferences) _ = Resolve(workspace.RootPath, reference);
        AtomicWrite(FlowPath(workspace, flow.Id), JsonSerializer.Serialize(flow, JsonOptions));
    }

    public FlowDocument ImportFlow(ProjectWorkspace workspace, string sourcePath, string? newName = null)
    {
        var imported = ReadJson<FlowDocument>(Path.GetFullPath(sourcePath));
        var nodeIds = imported.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        var copy = imported with
        {
            Id = Guid.NewGuid(),
            Name = newName ?? $"{imported.Name} 副本",
            Nodes = imported.Nodes.Select(node => node with { Id = nodeIds[node.Id] }).ToList(),
            Edges = imported.Edges.Select(edge => edge with { Id = Guid.NewGuid(), FromNodeId = nodeIds[edge.FromNodeId], ToNodeId = nodeIds[edge.ToNodeId] }).ToList(),
            AssetReferences = [.. imported.AssetReferences]
        };
        foreach (var reference in copy.AssetReferences) _ = Resolve(workspace.RootPath, reference);
        return copy;
    }

    public string ExportFlow(FlowDocument flow, string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        AtomicWrite(destination, JsonSerializer.Serialize(flow, JsonOptions));
        return destination;
    }

    public string SaveRun(ProjectWorkspace workspace, WorkflowExecutionRun run)
    {
        var path = Resolve(workspace.RootPath, $"runs/{run.Id:N}.run.json");
        AtomicWrite(path, JsonSerializer.Serialize(run, JsonOptions));
        return path;
    }

    public string ImportImage(ProjectWorkspace workspace, string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("图像资源不存在。", source);
        if (new FileInfo(source).Length > MaxImportBytes) throw new InvalidDataException("图像文件超出 2 GB 大小限制。");
        var safeName = string.Concat(Path.GetFileNameWithoutExtension(source).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var relative = $"assets/images/{Guid.NewGuid():N}_{safeName}{Path.GetExtension(source).ToLowerInvariant()}";
        File.Copy(source, Resolve(workspace.RootPath, relative));
        return relative;
    }

    public void CreateFolder(ProjectWorkspace workspace, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidDataException("文件夹名称不能为空。");
        Directory.CreateDirectory(Resolve(workspace.RootPath, relativePath));
    }

    public IReadOnlyList<ProjectFileEntry> ListFiles(ProjectWorkspace workspace)
    {
        var result = new List<ProjectFileEntry>();
        var pending = new Stack<string>();
        pending.Push(workspace.RootPath);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || IsInternalPath(path)) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                result.Add(new ProjectFileEntry(Path.GetRelativePath(workspace.RootPath, path).Replace('\\', '/'), isDirectory, isDirectory ? 0 : new FileInfo(path).Length));
                if (isDirectory) pending.Push(path);
            }
        }
        return result.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void RenameOrMove(ProjectWorkspace workspace, string oldRelativePath, string newRelativePath)
    {
        var oldPath = Resolve(workspace.RootPath, oldRelativePath);
        var newPath = Resolve(workspace.RootPath, newRelativePath);
        if (!File.Exists(oldPath) && !Directory.Exists(oldPath)) throw new FileNotFoundException("资源不存在。", oldPath);
        if (File.Exists(newPath) || Directory.Exists(newPath)) throw new IOException("目标路径已存在。");
        var isDirectory = Directory.Exists(oldPath);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        if (isDirectory) Directory.Move(oldPath, newPath); else File.Move(oldPath, newPath);

        try
        {
            var oldPrefix = oldRelativePath.TrimEnd('/', '\\').Replace('\\', '/') + (isDirectory ? "/" : string.Empty);
            var newPrefix = newRelativePath.TrimEnd('/', '\\').Replace('\\', '/') + (isDirectory ? "/" : string.Empty);
            foreach (var flow in ListFlows(workspace))
            {
                var references = flow.AssetReferences.Select(reference =>
                    string.Equals(reference, oldRelativePath, StringComparison.OrdinalIgnoreCase) ||
                    (isDirectory && reference.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                        ? newPrefix + (isDirectory ? reference[oldPrefix.Length..] : string.Empty)
                        : reference).ToList();
                if (!references.SequenceEqual(flow.AssetReferences)) SaveFlow(workspace, flow with { AssetReferences = references });
            }
        }
        catch
        {
            if (isDirectory) Directory.Move(newPath, oldPath); else File.Move(newPath, oldPath);
            throw;
        }
    }

    public void DeleteResource(ProjectWorkspace workspace, string relativePath)
    {
        var fullPath = Resolve(workspace.RootPath, relativePath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) throw new FileNotFoundException("资源不存在。", fullPath);
        var normalized = relativePath.Replace('\\', '/').TrimEnd('/') + (Directory.Exists(fullPath) ? "/" : string.Empty);
        if (ListFlows(workspace).Any(flow => flow.AssetReferences.Any(reference =>
                string.Equals(reference, relativePath, StringComparison.OrdinalIgnoreCase) ||
                (Directory.Exists(fullPath) && reference.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidOperationException("此资源仍被流程引用，已阻止删除。");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true); else File.Delete(fullPath);
    }

    public string ExportProject(ProjectWorkspace workspace, string archivePath)
    {
        var destination = Path.GetFullPath(archivePath);
        if (destination.StartsWith(Path.GetFullPath(workspace.RootPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("项目导出压缩包不能放在项目自身目录中。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ZipFile.CreateFromDirectory(workspace.RootPath, destination, CompressionLevel.Optimal, includeBaseDirectory: false);
        return destination;
    }

    public ProjectWorkspace ImportProject(string archivePath, string destinationParent, string projectName)
    {
        ValidateSegment(projectName, "项目名称");
        var destination = Path.Combine(Path.GetFullPath(destinationParent), projectName);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("导入目标已存在。");
        Directory.CreateDirectory(destination);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("项目压缩包文件数量超限。");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                total = checked(total + entry.Length);
                if (total > MaxImportBytes) throw new InvalidDataException("项目压缩包解压体积超过 2 GB。");
                var relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                if (relative.Length == 0) continue;
                var output = Resolve(destination, relative);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(output);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var input = entry.Open(); using var target = File.Create(output); input.CopyTo(target);
                }
            }
            var workspace = OpenProject(Resolve(destination, ProjectFileName));
            return workspace with { Project = SaveProject(workspace with { Project = workspace.Project with { Name = projectName } }) };
        }
        catch
        {
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private string FlowPath(ProjectWorkspace workspace, Guid id) => Resolve(workspace.RootPath, $"flows/{id:N}.flow.json");

    private static T ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException($"JSON 文件内容为空：{path}");
    }

    private static void AtomicWrite(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temp, contents); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.IndexOfAny(['\0']) >= 0) throw new InvalidDataException("项目资源必须使用安全的相对路径。");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("检测到项目路径穿越。");
        return fullPath;
    }

    private static void ValidateSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException($"{label}不能为空，且不能包含路径分隔符或非法字符。");
    }

    private static bool IsInternalPath(string path) => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
}
