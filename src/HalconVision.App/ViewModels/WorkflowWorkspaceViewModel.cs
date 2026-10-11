using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

namespace HalconVision.App;

public sealed class WorkflowWorkspaceViewModel : ObservableModel
{
    private readonly ProjectStore _projectStore = new();
    private readonly INodeRegistry _registry;
    private readonly WorkflowGraphValidator _validator;
    private readonly WorkflowExecutionEngine _engine;
    private ProjectWorkspace? _workspace;
    private FlowTabViewModel? _selectedFlow;
    private string _projectStatus = "未打开项目";
    private string _searchText = string.Empty;
    private string _nodeSearchText = string.Empty;
    private string _logText = "工作区就绪。";
    private WorkflowExecutionRun? _lastRun;
    private CancellationTokenSource? _runCancellation;
    private readonly Dictionary<(Guid FlowId, Guid NodeId), (string Folder, string[] Files, int NextIndex)> _imageSequenceCursors = [];

    public WorkflowWorkspaceViewModel()
    {
        var registry = BuiltInNodeCatalog.Create();
        registry.Register(new HalconNodeProvider(),
        [
            new HalconImageReadExecutor(),
            new HalconCameraCaptureExecutor(),
            new HalconThresholdExecutor(),
            new HalconBlobAnalysisExecutor()
        ]);
        _registry = registry;
        _validator = new WorkflowGraphValidator(_registry);
        _engine = new WorkflowExecutionEngine(_registry, _validator);
    }

    public ObservableCollection<FlowTabViewModel> Flows { get; } = [];
    public ObservableCollection<ProjectTreeItem> ProjectTree { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<NodeExecutionResult> ExecutionResults { get; } = [];
    public IReadOnlyCollection<NodeDefinition> NodeCatalog => _registry.Definitions;
    public IReadOnlyCollection<NodeDefinition> FilteredNodeCatalog => string.IsNullOrWhiteSpace(NodeSearchText)
        ? NodeCatalog
        : NodeCatalog.Where(node => node.DisplayName.Contains(NodeSearchText, StringComparison.OrdinalIgnoreCase) ||
            node.Description.Contains(NodeSearchText, StringComparison.OrdinalIgnoreCase) ||
            node.Category.Contains(NodeSearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
    public ProjectWorkspace? Workspace { get => _workspace; private set { if (Set(ref _workspace, value)) { Raise(nameof(ProjectName)); Raise(nameof(ProjectRoot)); Raise(nameof(HasProject)); } } }
    public string ProjectName => Workspace?.Project.Name ?? "未打开项目";
    public string ProjectRoot => Workspace?.ProjectFilePath ?? Workspace?.RootPath ?? string.Empty;
    public bool HasProject => Workspace is not null;
    public FlowTabViewModel? SelectedFlow { get => _selectedFlow; set => Set(ref _selectedFlow, value); }
    public string ProjectStatus { get => _projectStatus; set => Set(ref _projectStatus, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshProjectTree(); } }
    public string NodeSearchText { get => _nodeSearchText; set { if (Set(ref _nodeSearchText, value)) Raise(nameof(FilteredNodeCatalog)); } }
    public bool HasUnsavedFlows => Flows.Any(flow => flow.IsDirty);
    public bool IsRunning => _runCancellation is not null;
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public WorkflowExecutionRun? LastRun { get => _lastRun; private set { if (Set(ref _lastRun, value)) { Raise(nameof(RunStateText)); Raise(nameof(RunElapsedText)); } } }
    public string RunStateText => IsRunning ? "执行中" : LastRun?.Status.ToString() ?? "未运行";
    public string RunElapsedText => LastRun is null ? "—" : $"{(LastRun.EndedAtUtc - LastRun.StartedAtUtc).TotalMilliseconds:F1} ms";
    public void Log(string message) { LogText = $"{DateTime.Now:HH:mm:ss}  {message}"; Logs.Insert(0, LogText); }

    public ProjectWorkspace CreateProject(string projectFilePath, string name)
    {
        var workspace = _projectStore.CreateProject(projectFilePath, name);
        OpenWorkspace(workspace);
        return workspace;
    }

    public ProjectWorkspace OpenProject(string projectFile)
    {
        var workspace = _projectStore.OpenProject(projectFile);
        OpenWorkspace(workspace);
        return workspace;
    }

    public void SaveAll()
    {
        if (Workspace is null) return;
        Workspace = Workspace with { Project = _projectStore.SaveProject(Workspace, Flows.Select(flow => flow.Document).ToArray()) };
        foreach (var flow in Flows) flow.MarkSaved();
        RefreshProjectTree(); ProjectStatus = "项目已保存"; Raise(nameof(HasUnsavedFlows));
    }

    public FlowTabViewModel CreateFlow(string name)
    {
        if (Workspace is null) throw new InvalidOperationException("请先创建或打开项目。");
        var flow = _projectStore.CreateFlow(Workspace, name);
        var tab = new FlowTabViewModel(flow, _registry, _validator); Flows.Add(tab); SelectedFlow = tab;
        RefreshProjectTree(); ProjectStatus = $"已创建流程：{name}"; return tab;
    }

    public FlowTabViewModel ImportFlow(string path)
    {
        if (Workspace is null) throw new InvalidOperationException("请先打开项目。");
        var flow = _projectStore.ImportFlow(Workspace, path);
        var tab = new FlowTabViewModel(flow, _registry, _validator); Flows.Add(tab); SelectedFlow = tab;
        RefreshProjectTree(); ProjectStatus = $"已导入流程：{flow.Name}"; return tab;
    }

    public string ExportFlow(FlowTabViewModel flow, string path)
    {
        if (Workspace is null) throw new InvalidOperationException("请先打开项目。");
        return _projectStore.ExportFlow(flow.Document, path);
    }

    public void CloseProject()
    {
        Workspace = null; Flows.Clear(); SelectedFlow = null; ProjectTree.Clear(); ProjectStatus = "未打开项目";
    }

    public void RefreshProjectTree()
    {
        ProjectTree.Clear();
        if (Workspace is null) return;
        var root = new ProjectTreeItem(Workspace.Project.Name, string.Empty, true);
        var nodes = new Dictionary<string, ProjectTreeItem>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };
        foreach (var entry in _projectStore.ListFiles(Workspace))
        {
            if (!string.IsNullOrEmpty(SearchText) && !entry.RelativePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
                !entry.RelativePath.Split('/').Any(segment => segment.Contains(SearchText, StringComparison.OrdinalIgnoreCase))) continue;
            var parts = entry.RelativePath.Split('/'); var parentKey = string.Empty;
            for (var i = 0; i < parts.Length; i++)
            {
                var relative = string.Join('/', parts.Take(i + 1));
                if (!nodes.TryGetValue(relative, out var item))
                {
                    var isDirectory = i < parts.Length - 1 || entry.IsDirectory;
                    item = new ProjectTreeItem(parts[i], relative, isDirectory);
                    nodes[parentKey].Children.Add(item); nodes[relative] = item;
                }
                parentKey = relative;
            }
        }
        ProjectTree.Add(root);
    }

    public string ImportImage(string path)
    {
        if (Workspace is null) throw new InvalidOperationException("请先打开项目。");
        var relative = _projectStore.ImportImage(Workspace, path);
        if (SelectedFlow is not null)
        {
            var refs = SelectedFlow.Document.AssetReferences.Append(relative).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SelectedFlow.SetAssetReferences(refs);
        }
        RefreshProjectTree(); ProjectStatus = $"已导入资源：{relative}"; return relative;
    }

    public void CreateFolder(string relativePath)
    {
        if (Workspace is null) throw new InvalidOperationException("请先打开项目。");
        _projectStore.CreateFolder(Workspace, relativePath); RefreshProjectTree();
    }

    public void RenameOrMove(string oldPath, string newPath)
    {
        if (Workspace is null) throw new InvalidOperationException("请先打开项目。");
        var oldPrefix = oldPath.TrimEnd('/').Replace('\\', '/');
        var newPrefix = newPath.TrimEnd('/').Replace('\\', '/');
        _projectStore.RenameOrMove(Workspace, oldPath, newPath);
        foreach (var flow in Flows)
        {
            var updated = flow.Document.AssetReferences.Select(reference =>
                reference.Equals(oldPrefix, StringComparison.OrdinalIgnoreCase) ? newPrefix :
                reference.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase) ? newPrefix + reference[oldPrefix.Length..] : reference)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!updated.SequenceEqual(flow.Document.AssetReferences, StringComparer.OrdinalIgnoreCase)) flow.SetAssetReferences(updated);
        }
        RefreshProjectTree();
    }

    public void DeleteResource(string relativePath)
    {
        if (Workspace is null) throw new InvalidOperationException("请先打开项目。");
        var normalized = relativePath.TrimEnd('/').Replace('\\', '/');
        if (Flows.SelectMany(flow => flow.Document.AssetReferences).Any(reference => reference.Equals(normalized, StringComparison.OrdinalIgnoreCase) || reference.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("资源仍被当前打开的流程引用，请先移除引用后再删除。");
        _projectStore.DeleteResource(Workspace, relativePath); RefreshProjectTree();
    }

    public string ExportProject(string path)
    {
        if (Workspace is null) throw new InvalidOperationException("请先 open project.");
        SaveAll(); return _projectStore.ExportProject(Workspace, path);
    }

    public ProjectWorkspace ImportProject(string archivePath, string parent, string name)
    {
        var workspace = _projectStore.ImportProject(archivePath, parent, name); OpenWorkspace(workspace); return workspace;
    }

    public FlowDocument? PrepareNextImageFlow(out string? imagePath, out bool hasImageSequence, out bool sequenceEnded)
    {
        imagePath = null;
        hasImageSequence = false;
        sequenceEnded = false;
        if (SelectedFlow is null) return null;

        var flow = SelectedFlow.Document;
        var nodes = flow.Nodes.ToList();
        foreach (var node in flow.Nodes.Where(node => node.TypeId == HalconNodeProvider.ImageRead))
        {
            if (!node.Parameters.TryGetValue("path", out var value) || value.ValueKind != JsonValueKind.String) continue;
            var source = value.GetString();
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) continue;

            var fullFolder = Path.GetFullPath(source);
            var files = Directory.EnumerateFiles(fullFolder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".bmp" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".jp2")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidDataException($"文件夹中没有支持的图像：{fullFolder}");

            hasImageSequence = true;
            var key = (flow.Id, node.Id);
            if (!_imageSequenceCursors.TryGetValue(key, out var cursor) || !string.Equals(cursor.Folder, fullFolder, StringComparison.OrdinalIgnoreCase))
                cursor = (fullFolder, files, 0);
            else if (!cursor.Files.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
                cursor = (fullFolder, files, Math.Min(cursor.NextIndex, files.Length));

            if (cursor.NextIndex >= files.Length)
            {
                _imageSequenceCursors[key] = (fullFolder, files, 0);
                sequenceEnded = true;
                imagePath = null;
                return null;
            }

            imagePath = files[cursor.NextIndex];
            _imageSequenceCursors[key] = (fullFolder, files, cursor.NextIndex + 1);
            var parameters = new Dictionary<string, System.Text.Json.JsonElement>(node.Parameters)
            {
                ["path"] = JsonSerializer.SerializeToElement(imagePath)
            };
            nodes[nodes.FindIndex(item => item.Id == node.Id)] = node with { Parameters = parameters };
        }
        return flow with { Nodes = nodes };
    }

    public void ReturnImageToSequence(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || SelectedFlow is null) return;
        foreach (var key in _imageSequenceCursors.Keys.ToArray())
        {
            var cursor = _imageSequenceCursors[key];
            if (key.FlowId == SelectedFlow.Id && cursor.NextIndex > 0 &&
                string.Equals(cursor.Files[cursor.NextIndex - 1], imagePath, StringComparison.OrdinalIgnoreCase))
                _imageSequenceCursors[key] = (cursor.Folder, cursor.Files, cursor.NextIndex - 1);
        }
    }

    public async Task<WorkflowExecutionRun> RunSelectedFlowAsync(IProgress<NodeExecutionResult>? progress = null, FlowDocument? flowOverride = null)
    {
        if (Workspace is null || SelectedFlow is null) throw new InvalidOperationException("请先打开项目和流程。");
        _runCancellation = new CancellationTokenSource(); Raise(nameof(IsRunning)); Raise(nameof(RunStateText));
        try
        {
            var run = await _engine.RunAsync(flowOverride ?? SelectedFlow.Document, progress, _runCancellation.Token);
            return run;
        }
        finally { _runCancellation.Dispose(); _runCancellation = null; Raise(nameof(IsRunning)); Raise(nameof(RunStateText)); }
    }

    public void CancelRun() => _runCancellation?.Cancel();
    public void SetExecutionRun(WorkflowExecutionRun run)
    {
        if (!ReferenceEquals(LastRun, run)) LastRun?.Dispose();
        LastRun = run;
        ExecutionResults.Clear();
        foreach (var result in run.NodeResults) ExecutionResults.Add(result);
        Raise(nameof(ExecutionResults));
    }

    private void OpenWorkspace(ProjectWorkspace workspace)
    {
        var flows = _projectStore.ListFlows(workspace);
        if (flows.Count == 0) throw new InvalidDataException("项目中没有流程文件。");
        LastRun?.Dispose();
        LastRun = null;
        _imageSequenceCursors.Clear();
        var tabs = flows.Select(flow => new FlowTabViewModel(flow, _registry, _validator)).ToArray();
        Workspace = workspace; Flows.Clear(); foreach (var tab in tabs) Flows.Add(tab);
        SelectedFlow = tabs.FirstOrDefault(tab => tab.Id.ToString("N") == workspace.Project.DefaultFlowId) ?? tabs[0];
        RefreshProjectTree(); ProjectStatus = $"已打开项目：{workspace.Project.Name}";
    }

    public void Dispose() => LastRun?.Dispose();
}
