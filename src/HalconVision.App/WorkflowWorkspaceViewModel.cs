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

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed class ProjectTreeItem(string name, string relativePath, bool isDirectory) : ObservableModel
{
    public string Name { get; } = name;
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = isDirectory;
    public ObservableCollection<ProjectTreeItem> Children { get; } = [];
}

public sealed class NodePortViewModel(Guid nodeId, string id, string name, PortDirection direction, string dataType, bool required) : ObservableModel
{
    public Guid NodeId { get; } = nodeId;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public PortDirection Direction { get; } = direction;
    public string DataType { get; } = dataType;
    public bool Required { get; } = required;
}

public sealed class NodeParameterViewModel : ObservableModel
{
    private string _valueText;
    private string? _error;
    private readonly FlowTabViewModel _owner;
    private readonly Guid _nodeId;

    public NodeParameterViewModel(FlowTabViewModel owner, Guid nodeId, NodeParameterDefinition definition, JsonElement value)
    {
        _owner = owner; _nodeId = nodeId; Definition = definition;
        _valueText = Format(value);
    }

    public NodeParameterDefinition Definition { get; }
    public string Name => Definition.Name;
    public string DataType => Definition.DataType;
    public bool IsPath => string.Equals(DataType, "path", StringComparison.Ordinal);
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public void Commit()
    {
        try
        {
            var element = Definition.DataType switch
            {
                "string" or "path" => JsonSerializer.SerializeToElement(ValueText),
                "number" when double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
                "bool" when bool.TryParse(ValueText, out var boolean) => JsonSerializer.SerializeToElement(boolean),
                _ => throw new FormatException($"请输入有效的 {Definition.DataType} 值。")
            };
            _owner.SetParameter(_nodeId, Definition.Id, element);
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
        _ => value.GetRawText()
    };
}

public sealed class FlowNodeViewModel : ObservableModel
{
    private double _x;
    private double _y;
    private string _displayName;
    private bool _isSelected;
    private string _status = "Idle";
    private readonly FlowTabViewModel _owner;

    public FlowNodeViewModel(FlowTabViewModel owner, NodeInstance node, NodeDefinition? definition)
    {
        _owner = owner; Id = node.Id; TypeId = node.TypeId; TypeVersion = node.TypeVersion;
        _displayName = node.DisplayName; _x = node.X; _y = node.Y; Enabled = node.Enabled;
        Definition = definition;
        InputPorts = new ObservableCollection<NodePortViewModel>((definition?.Ports ?? []).Where(port => port.Direction == PortDirection.Input)
            .Select(port => new NodePortViewModel(Id, port.Id, port.Name, port.Direction, port.DataType, port.Required)));
        OutputPorts = new ObservableCollection<NodePortViewModel>((definition?.Ports ?? []).Where(port => port.Direction == PortDirection.Output)
            .Select(port => new NodePortViewModel(Id, port.Id, port.Name, port.Direction, port.DataType, port.Required)));
        Parameters = new ObservableCollection<NodeParameterViewModel>((definition?.Parameters ?? []).Select(parameter =>
            new NodeParameterViewModel(owner, Id, parameter,
                node.Parameters.TryGetValue(parameter.Id, out var value) ? value : parameter.DefaultValue ?? JsonSerializer.SerializeToElement(string.Empty))));
    }

    public Guid Id { get; }
    public string TypeId { get; }
    public int TypeVersion { get; }
    public NodeDefinition? Definition { get; }
    public string Category => Definition?.Category ?? "缺少节点定义";
    public ObservableCollection<NodePortViewModel> InputPorts { get; }
    public ObservableCollection<NodePortViewModel> OutputPorts { get; }
    public ObservableCollection<NodeParameterViewModel> Parameters { get; }
    public double X { get => _x; private set => Set(ref _x, value); }
    public double Y { get => _y; private set => Set(ref _y, value); }
    public string DisplayName { get => _displayName; set { if (Set(ref _displayName, value)) _owner.RenameNode(Id, value); } }
    public bool Enabled { get; private set; }
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }
    public string Status { get => _status; internal set => Set(ref _status, value); }
    internal void MoveTo(double x, double y) { X = Math.Max(0, x); Y = Math.Max(0, y); }
}

public sealed record FlowEdgeViewModel(double X1, double Y1, double X2, double Y2, string State);

public sealed class FlowTabViewModel : ObservableModel
{
    private readonly INodeRegistry _registry;
    private readonly WorkflowGraphValidator _validator;
    private FlowDocument _document;
    private bool _isDirty;
    private double _zoom = 0.85;
    private FlowNodeViewModel? _selectedNode;
    private readonly Stack<string> _undo = [];
    private readonly Stack<string> _redo = [];
    private List<NodeInstance> _clipboardNodes = [];
    private List<FlowEdge> _clipboardEdges = [];
    private readonly HashSet<Guid> _selection = [];

    public FlowTabViewModel(FlowDocument document, INodeRegistry registry, WorkflowGraphValidator validator)
    {
        _document = document; _registry = registry; _validator = validator; Rebuild();
    }

    public Guid Id => _document.Id;
    public string Name => _document.Name;
    public FlowDocument Document => _document;
    public ObservableCollection<FlowNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<FlowEdgeViewModel> Edges { get; } = [];
    public ObservableCollection<NodeParameterViewModel> SelectedParameters => SelectedNode?.Parameters ?? EmptyParameters;
    public FlowNodeViewModel? SelectedNode { get => _selectedNode; private set { if (Set(ref _selectedNode, value)) Raise(nameof(SelectedParameters)); } }
    public bool IsDirty { get => _isDirty; private set { if (Set(ref _isDirty, value)) Raise(nameof(TabLabel)); } }
    public string TabLabel => $"{Name}{(IsDirty ? " ●" : string.Empty)}";
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public double Zoom { get => _zoom; private set => Set(ref _zoom, Math.Clamp(value, 0.25, 1.8)); }
    public void AdjustZoom(double delta) => Zoom += delta;
    public void SetZoom(double zoom) => Zoom = zoom;
    public IReadOnlyList<WorkflowValidationIssue> ValidationIssues => _validator.Validate(_document);
    private static ObservableCollection<NodeParameterViewModel> EmptyParameters { get; } = [];

    public void AddNode(NodeDefinition definition)
    {
        PushUndo();
        var index = _document.Nodes.Count;
        var parameters = definition.Parameters.Where(item => item.DefaultValue is not null)
            .ToDictionary(item => item.Id, item => item.DefaultValue!.Value.Clone());
        var node = new NodeInstance
        {
            TypeId = definition.TypeId, TypeVersion = definition.Version, DisplayName = definition.DisplayName,
            X = 80 + (index % 4) * 45, Y = 80 + (index % 5) * 45, Parameters = parameters
        };
        _document = _document with { Nodes = [.. _document.Nodes, node] };
        _selection.Clear(); _selection.Add(node.Id); IsDirty = true; Rebuild();
    }

    public bool Connect(Guid fromNodeId, string fromPortId, Guid toNodeId, string toPortId, out string error)
    {
        var edge = new FlowEdge(Guid.NewGuid(), fromNodeId, fromPortId, toNodeId, toPortId);
        var candidate = _document with { Edges = [.. _document.Edges, edge] };
        var issues = _validator.Validate(candidate);
        var relevant = issues.FirstOrDefault(issue => issue.EdgeId == edge.Id || issue.Message.Contains("有向环", StringComparison.Ordinal));
        if (relevant is not null) { error = relevant.Message; return false; }
        PushUndo(); _document = candidate; IsDirty = true; Rebuild(); error = string.Empty; return true;
    }

    public void SelectNode(Guid id, bool extend)
    {
        if (!extend) _selection.Clear();
        if (extend && _selection.Contains(id)) _selection.Remove(id); else _selection.Add(id);
        UpdateSelection();
    }

    public void BeginMove() => PushUndo();

    public void MoveNode(Guid id, double x, double y)
    {
        var node = Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null) return;
        node.MoveTo(x, y);
        _document = _document with { Nodes = _document.Nodes.Select(item => item.Id == id ? item with { X = node.X, Y = node.Y } : item).ToList() };
        IsDirty = true; RefreshEdges();
    }

    public void RenameNode(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _document = _document with { Nodes = _document.Nodes.Select(node => node.Id == id ? node with { DisplayName = name } : node).ToList() };
        IsDirty = true;
    }

    public void SetParameter(Guid nodeId, string key, JsonElement value)
    {
        PushUndo();
        _document = _document with
        {
            Nodes = _document.Nodes.Select(node =>
            {
                if (node.Id != nodeId) return node;
                var parameters = new Dictionary<string, JsonElement>(node.Parameters) { [key] = value.Clone() };
                return node with { Parameters = parameters };
            }).ToList()
        };
        IsDirty = true;
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        PushUndo();
        _document = _document with
        {
            Nodes = _document.Nodes.Where(node => !_selection.Contains(node.Id)).ToList(),
            Edges = _document.Edges.Where(edge => !_selection.Contains(edge.FromNodeId) && !_selection.Contains(edge.ToNodeId)).ToList()
        };
        _selection.Clear(); IsDirty = true; Rebuild();
    }

    public void CopySelection()
    {
        _clipboardNodes = _document.Nodes.Where(node => _selection.Contains(node.Id)).ToList();
        _clipboardEdges = _document.Edges.Where(edge => _selection.Contains(edge.FromNodeId) && _selection.Contains(edge.ToNodeId)).ToList();
    }

    public void PasteSelection()
    {
        if (_clipboardNodes.Count == 0) return;
        PushUndo();
        var ids = _clipboardNodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        var nodes = _clipboardNodes.Select(node => node with { Id = ids[node.Id], X = node.X + 40, Y = node.Y + 40, Parameters = new Dictionary<string, JsonElement>(node.Parameters) }).ToList();
        var edges = _clipboardEdges.Select(edge => edge with { Id = Guid.NewGuid(), FromNodeId = ids[edge.FromNodeId], ToNodeId = ids[edge.ToNodeId] }).ToList();
        _document = _document with { Nodes = [.. _document.Nodes, .. nodes], Edges = [.. _document.Edges, .. edges] };
        _selection.Clear(); foreach (var node in nodes) _selection.Add(node.Id);
        IsDirty = true; Rebuild();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Serialize()); _document = JsonSerializer.Deserialize<FlowDocument>(_undo.Pop())!;
        _selection.Clear(); IsDirty = true; Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); Rebuild();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Serialize()); _document = JsonSerializer.Deserialize<FlowDocument>(_redo.Pop())!;
        _selection.Clear(); IsDirty = true; Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); Rebuild();
    }

    public void MarkSaved() => IsDirty = false;

    public void SetAssetReferences(IReadOnlyList<string> references)
    {
        PushUndo(); _document = _document with { AssetReferences = [.. references] }; IsDirty = true;
        Raise(nameof(Document));
    }

    public void ApplyNodeResult(NodeExecutionResult result)
    {
        var node = Nodes.FirstOrDefault(item => item.Id == result.NodeId);
        if (node is not null) node.Status = result.Status.ToString().ToUpperInvariant();
    }

    private void PushUndo()
    {
        _undo.Push(Serialize()); _redo.Clear();
        while (_undo.Count > 100)
        {
            var keep = _undo.Reverse().Skip(1).ToArray(); _undo.Clear(); foreach (var item in keep) _undo.Push(item);
        }
        Raise(nameof(CanUndo)); Raise(nameof(CanRedo));
    }

    private string Serialize() => JsonSerializer.Serialize(_document);

    private void Rebuild()
    {
        Nodes.Clear();
        foreach (var node in _document.Nodes)
        {
            _registry.TryGetDefinition(node.TypeId, node.TypeVersion, out var definition);
            var viewModel = new FlowNodeViewModel(this, node, definition) { Status = node.Enabled ? "IDLE" : "DISABLED" };
            Nodes.Add(viewModel);
        }
        UpdateSelection(); RefreshEdges();
        Raise(nameof(Document)); Raise(nameof(ValidationIssues));
    }

    private void UpdateSelection()
    {
        foreach (var node in Nodes) node.IsSelected = _selection.Contains(node.Id);
        SelectedNode = Nodes.FirstOrDefault(node => _selection.Contains(node.Id));
    }

    private void RefreshEdges()
    {
        Edges.Clear();
        foreach (var edge in _document.Edges)
        {
            var source = Nodes.FirstOrDefault(node => node.Id == edge.FromNodeId);
            var target = Nodes.FirstOrDefault(node => node.Id == edge.ToNodeId);
            if (source is null || target is null) continue;
            var outIndex = source.OutputPorts.ToList().FindIndex(port => port.Id == edge.FromPortId);
            var inIndex = target.InputPorts.ToList().FindIndex(port => port.Id == edge.ToPortId);
            if (outIndex < 0 || inIndex < 0) continue;
            Edges.Add(new FlowEdgeViewModel(source.X + 152, source.Y + 38 + outIndex * 18, target.X + 12, target.Y + 38 + inIndex * 18, "#58B7B2"));
        }
    }
}

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
