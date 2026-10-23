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
    public IReadOnlyList<WorkflowValidationIssue> ValidateDocument(FlowDocument document) => _validator.Validate(document);
    public void SetInspectionPlan(IEnumerable<InspectionRegion> regions, IEnumerable<InspectionTask> tasks)
    {
        PushUndo();
        _document = _document with { SchemaVersion = 2, InspectionRegions = regions.ToList(), InspectionTasks = tasks.ToList() };
        foreach (var parameter in Nodes.SelectMany(n => n.Parameters)) parameter.RefreshInspectionBinding();
        IsDirty = true; Raise(nameof(Document)); Raise(nameof(ValidationIssues));
    }
    public void EnsureRoiEditsValid()
    {
        foreach (var node in Nodes)
            foreach (var parameter in node.Parameters.Where(item => item.IsRoiList))
            {
                if (parameter.Error is not null)
                    throw new InvalidDataException($"{Name} / {node.DisplayName}：{parameter.Error}");
                try { parameter.ReadRois(); }
                catch (Exception ex) { throw new InvalidDataException($"{Name} / {node.DisplayName}：ROI 列表无效，{ex.Message}", ex); }
            }
    }
    public ObservableCollection<FlowNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<FlowEdgeViewModel> Edges { get; } = [];
    public ObservableCollection<NodeParameterViewModel> SelectedParameters => SelectedNode?.Parameters ?? EmptyParameters;
    public FlowNodeViewModel? SelectedNode { get => _selectedNode; private set { if (Set(ref _selectedNode, value)) Raise(nameof(SelectedParameters)); } }
    public bool IsDirty { get => _isDirty; private set { if (Set(ref _isDirty, value)) Raise(nameof(TabLabel)); } }
    public string TabLabel => $"{Name}{(IsDirty ? " ●" : string.Empty)}";
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public double Zoom { get => _zoom; private set => Set(ref _zoom, Math.Clamp(value, 0.1, 1.8)); }
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
        var target = _document.Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (target is null || target.Parameters.TryGetValue(key, out var current) && current.GetRawText() == value.GetRawText()) return;
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
        Raise(nameof(ValidationIssues));
    }

    public int DeleteSelection()
    {
        var selectedIds = _selection.Count > 0 ? _selection.ToHashSet() : SelectedNode is null ? [] : [SelectedNode.Id];
        if (selectedIds.Count == 0) return 0;
        PushUndo();
        _document = _document with
        {
            Nodes = _document.Nodes.Where(node => !selectedIds.Contains(node.Id)).ToList(),
            Edges = _document.Edges.Where(edge => !selectedIds.Contains(edge.FromNodeId) && !selectedIds.Contains(edge.ToNodeId)).ToList()
        };
        _selection.Clear(); IsDirty = true; Rebuild();
        return selectedIds.Count;
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
        var nodes = _clipboardNodes.Select(node => PositionReferenceParameters.Remap(node with { Id = ids[node.Id], X = node.X + 40, Y = node.Y + 40, Parameters = new Dictionary<string, JsonElement>(node.Parameters) }, ids)).ToList();
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

    public void SetDetectionPanels(IEnumerable<DetectionPanelSettings> panels)
    {
        _document = _document with { DetectionPanels = panels.ToList() };
        IsDirty = true;
        Raise(nameof(Document));
    }

    public void SetAssetReferences(IReadOnlyList<string> references)
    {
        PushUndo(); _document = _document with { AssetReferences = [.. references] }; IsDirty = true;
        Raise(nameof(Document));
    }

    public void ApplyNodeResult(NodeExecutionResult result)
    {
        var node = Nodes.FirstOrDefault(item => item.Id == result.NodeId);
        if (node is not null) node.Status = NodeResultSummary.Describe(result);
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
