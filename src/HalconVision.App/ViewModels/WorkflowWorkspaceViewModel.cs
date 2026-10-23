using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

namespace HalconVision.App;

public sealed class WorkflowWorkspaceViewModel : ObservableModel
{
    private readonly HalconCameraManager _cameraManager = new();
    private readonly DlModelSessionCache _dlSessions = new();
    private readonly ShapeMatchModelCache _matchModels = new();
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
    private Task<WorkflowExecutionRun>? _activeRunTask;
    private readonly HashSet<Task> _cameraOperations = [];
    private bool _shuttingDown;
    private bool _disposed;
    private int _cameraScanGeneration;
    private readonly Dictionary<(Guid FlowId, Guid NodeId), (string Folder, string[] Files, int NextIndex)> _imageSequenceCursors = [];

    public WorkflowWorkspaceViewModel()
    {
        var registry = BuiltInNodeCatalog.Create();
        registry.Register(new HalconNodeProvider(),
        [
            new HalconImageReadExecutor(),
            new HalconCameraCaptureExecutor(_cameraManager),
            new HalconThresholdExecutor(),
            new HalconBlobAnalysisExecutor(),
            new HalconStraightLineFindExecutor(),
            new HalconSemanticSegmentationExecutor(_dlSessions),
            new HalconClassificationExecutor(_dlSessions),
            new HalconObjectDetectionExecutor(_dlSessions),
            new HalconQuickMatchExecutor(_matchModels), new HalconPositionCorrectionExecutor()
        ]);
        registry.Register(new HalconCircleNodeProvider(), [new HalconCircleFindExecutor()]);
        _registry = registry;
        _validator = new WorkflowGraphValidator(_registry);
        _engine = new WorkflowExecutionEngine(_registry, _validator);
        foreach (var item in _cameraManager.GetInstalledInterfaces()) CameraInterfaces.Add(item);
        SelectedCameraInterface = CameraInterfaces.FirstOrDefault(item => item.Id == "DirectShow") ?? CameraInterfaces.FirstOrDefault();
    }

    public ObservableCollection<FlowTabViewModel> Flows { get; } = [];
    public ObservableCollection<ProjectTreeItem> ProjectTree { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<NodeExecutionResult> ExecutionResults { get; } = [];
    public ObservableCollection<HalconAcquisitionInterface> CameraInterfaces { get; } = [];
    public ObservableCollection<HalconCameraDevice> CameraDevices { get; } = [];
    private HalconAcquisitionInterface? _selectedCameraInterface;
    private string? _preferredCameraDeviceId;
    public HalconAcquisitionInterface? SelectedCameraInterface
    {
        get => _selectedCameraInterface;
        set
        {
            if (!Set(ref _selectedCameraInterface, value)) return;
            CameraDevices.Clear();
            SelectedCameraDevice = null;
            if (value is not null && _cameraManager.ConnectedInterfaceId is { } connected && connected != value.Id)
                DisconnectCamera();
            CommitSelectedCameraInterface();
        }
    }
    private HalconCameraDevice? _selectedCameraDevice;
    private string _cameraStatus = "未连接";
    public HalconCameraDevice? SelectedCameraDevice
    {
        get => _selectedCameraDevice;
        set
        {
            if (!Set(ref _selectedCameraDevice, value)) return;
            if (value is not null && _cameraManager.ConnectedDeviceId is { } connected && connected != value.Id)
                DisconnectCamera();
            CommitSelectedCameraDevice();
        }
    }
    public string CameraStatus { get => _cameraStatus; private set => Set(ref _cameraStatus, value); }
    public bool IsCameraConnected => _cameraManager.ConnectedDeviceId is not null;
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
    public FlowTabViewModel? SelectedFlow
    {
        get => _selectedFlow;
        set
        {
            if (_selectedFlow is not null) _selectedFlow.PropertyChanged -= InspectionConfiguration_Changed;
            Set(ref _selectedFlow, value);
            if (_selectedFlow is not null) _selectedFlow.PropertyChanged += InspectionConfiguration_Changed;
            RefreshProductVerdict();
        }
    }
    private void InspectionConfiguration_Changed(object? sender, PropertyChangedEventArgs e) => RefreshProductVerdict();
    public string ProjectStatus { get => _projectStatus; set => Set(ref _projectStatus, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshProjectTree(); } }
    public string NodeSearchText { get => _nodeSearchText; set { if (Set(ref _nodeSearchText, value)) Raise(nameof(FilteredNodeCatalog)); } }
    public bool HasUnsavedFlows => Flows.Any(flow => flow.IsDirty);
    public bool IsRunning => _runCancellation is not null;
    private bool _isContinuousRunning;
    public bool IsContinuousRunning
    {
        get => _isContinuousRunning;
        set { if (Set(ref _isContinuousRunning, value)) { Raise(nameof(CanConfigureCamera)); Raise(nameof(RunStateText)); RefreshProductVerdict(); } }
    }
    public bool CanConfigureCamera => !IsRunning && !IsContinuousRunning && !_shuttingDown && _cameraOperations.Count == 0;
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public WorkflowExecutionRun? LastRun { get => _lastRun; private set { if (Set(ref _lastRun, value)) { Raise(nameof(RunStateText)); Raise(nameof(RunElapsedText)); RefreshProductVerdict(); } } }
    public string RunStateText => IsContinuousRunning ? "连续运行中" : IsRunning ? "执行中" :
        LastRun?.Status == WorkflowRunStatus.Success && LastRun.NodeResults.Any(r => r.Outputs.TryGetValue("status",out var s) &&
            s.ValueKind == JsonValueKind.Object && s.TryGetProperty("Code",out var c) && QuickMatchStatus.IsKnown(c.GetString()) && c.GetString() is not (QuickMatchStatus.Found or "MATCH_FOUND"))
            ? "执行完成 · 匹配未通过" : LastRun?.Status.ToString() ?? "未运行";
    public string RunElapsedText => LastRun is null ? "—" : $"{(LastRun.EndedAtUtc - LastRun.StartedAtUtc).TotalMilliseconds:F1} ms";
    public string ProductVerdictText => IsRunning || IsContinuousRunning ? "产品：检测中" : LastRun?.Inspection is { } inspection && SelectedFlow is { } flow && LastRun.FlowId == flow.Id
        ? inspection.Verdict == InspectionVerdict.NotConfigured && flow.Document.InspectionTasks.Count > 0 ? "产品：仅执行上游 · 未完成检测" : inspection.Verdict != InspectionVerdict.NotConfigured && inspection.ConfigurationIdentity != InspectionPlan.ConfigurationIdentity(flow.Document)
            ? "产品：配置已更改 · 请重新运行" : $"产品：{inspection.Label}" + (inspection.Tasks.Count > 0 ? $" · {inspection.Tasks.Count} 项任务" : "") : "产品：未运行";
    public System.Windows.Media.Brush ProductVerdictBrush => ProductVerdictText.StartsWith("产品：OK", StringComparison.Ordinal) ? System.Windows.Media.Brushes.LightGreen : ProductVerdictText.StartsWith("产品：NG", StringComparison.Ordinal) ? System.Windows.Media.Brushes.OrangeRed : ProductVerdictText.StartsWith("产品：ERROR", StringComparison.Ordinal) ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.LightGray;
    private void RefreshProductVerdict() { Raise(nameof(ProductVerdictText)); Raise(nameof(ProductVerdictBrush)); }
    public const int MaximumLogEntries = 500;
    public void Log(string message)
    {
        LogText = $"{DateTime.Now:HH:mm:ss}  {message}";
        Logs.Insert(0, LogText);
        while (Logs.Count > MaximumLogEntries) Logs.RemoveAt(Logs.Count - 1);
    }

    public async Task ScanCamerasAsync()
    {
        EnsureActive();
        var generation = ++_cameraScanGeneration;
        var cameraInterface = SelectedCameraInterface ?? throw new InvalidOperationException("HALCON 没有可用的相机类别。");
        var devices = await CameraOperationAsync(() => _cameraManager.Scan(cameraInterface.Id));
        if (_shuttingDown || generation != _cameraScanGeneration || SelectedCameraInterface?.Id != cameraInterface.Id) return;
        CameraDevices.Clear();
        foreach (var device in devices) CameraDevices.Add(device);
        SelectedCameraDevice = devices.FirstOrDefault(device => device.Id == _preferredCameraDeviceId)
            ?? devices.FirstOrDefault(device => device.Id == _selectedCameraDevice?.Id)
            ?? devices.FirstOrDefault();
        CameraStatus = devices.Count == 0 ? "未发现相机" : $"发现 {devices.Count} 台相机";
    }

    public async Task<HImage> ConnectSelectedCameraAsync()
    {
        EnsureActive();
        if (IsRunning) throw new InvalidOperationException("请先停止流程，再连接相机。");
        var cameraInterface = SelectedCameraInterface ?? throw new InvalidOperationException("请先选择相机类别。");
        var device = SelectedCameraDevice ?? throw new InvalidOperationException("请先扫描并选择相机。");
        var timeoutParameter = SelectedFlow?.SelectedNode?.Parameters.FirstOrDefault(parameter => parameter.Definition.Id == "grabTimeoutMs");
        var timeout = 2000;
        if (timeoutParameter is not null && (!int.TryParse(timeoutParameter.ValueText, out timeout) || timeout < 100 || timeout > 60000))
            throw new InvalidOperationException("取图超时必须是 100 到 60000 毫秒之间的整数。");
        HImage image;
        try { image = await CameraOperationAsync(() => _cameraManager.ConnectAndGrabPreview(cameraInterface.Id, device.Id, device.DeviceParameter, timeout)); }
        catch (Exception ex) { CameraStatus = $"连接/取图失败：{ex.Message}"; Raise(nameof(IsCameraConnected)); throw; }
        if (_shuttingDown)
        {
            image.Dispose();
            throw new OperationCanceledException("窗口正在关闭，已释放相机预览图像。");
        }
        CameraStatus = $"已连接并取到首帧：{device.Name}";
        Raise(nameof(IsCameraConnected));
        CommitSelectedCameraInterface();
        CommitSelectedCameraDevice();
        return image;
    }

    public void DisconnectCamera()
    {
        _cameraManager.Disconnect();
        CameraStatus = "未连接";
        Raise(nameof(IsCameraConnected));
    }

    public void LoadSelectedCameraDevice()
    {
        if (!CanConfigureCamera) return;
        var selectedNode = SelectedFlow?.SelectedNode;
        if (selectedNode?.TypeId != HalconNodeProvider.CameraCapture) return;
        var interfaceId = selectedNode.Parameters.FirstOrDefault(parameter => parameter.Definition.Id == "interface")?.ValueText;
        SelectedCameraInterface = CameraInterfaces.FirstOrDefault(item => item.Id == interfaceId) ?? SelectedCameraInterface;
        _preferredCameraDeviceId = selectedNode.Parameters.FirstOrDefault(parameter => parameter.Definition.Id == "device")?.ValueText;
        SelectedCameraDevice = CameraDevices.FirstOrDefault(device => device.Id == _preferredCameraDeviceId) ?? CameraDevices.FirstOrDefault();
    }

    private void CommitSelectedCameraInterface()
    {
        if (SelectedCameraInterface is null) return;
        var parameter = SelectedFlow?.SelectedNode?.Parameters.FirstOrDefault(item => item.Definition.Id == "interface");
        if (parameter is null) return;
        parameter.ValueText = SelectedCameraInterface.Id;
        parameter.Commit();
    }

    private void CommitSelectedCameraDevice()
    {
        if (SelectedCameraDevice is null) return;
        var parameter = SelectedFlow?.SelectedNode?.Parameters.FirstOrDefault(item => item.Definition.Id == "device");
        if (parameter is null) return;
        parameter.ValueText = SelectedCameraDevice.Id;
        parameter.Commit();
    }

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
        foreach (var flow in Flows) flow.EnsureRoiEditsValid();
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
        flow.EnsureRoiEditsValid();
        return _projectStore.ExportFlow(flow.Document, path);
    }

    public void CloseProject()
    {
        ReleaseModelSessions();
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
        => PrepareNextImageFlow(SelectedFlow?.Document, out imagePath, out hasImageSequence, out sequenceEnded);

    private FlowDocument? PrepareNextImageFlow(FlowDocument? sourceFlow, out string? imagePath, out bool hasImageSequence, out bool sequenceEnded)
    {
        imagePath = null;
        hasImageSequence = false;
        sequenceEnded = false;
        if (SelectedFlow is null || sourceFlow is null) return null;
        SelectedFlow.EnsureRoiEditsValid();
        var flow = sourceFlow;
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

    /// <summary>Execute only the connected image producer and its dependencies, never the untrained matcher.</summary>
    public async Task RunTemplateUpstreamAsync(Guid matchingNodeId)
    {
        var flow = SelectedFlow?.Document ?? throw new InvalidOperationException("请先选择流程。");
        var edge = flow.Edges.SingleOrDefault(e => e.ToNodeId == matchingNodeId && e.ToPortId == "image")
            ?? throw new InvalidOperationException("请先连接快捷匹配的图像输入。");
        var ancestors = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (!ancestors.Add(id)) return;
            foreach (var input in flow.Edges.Where(e => e.ToNodeId == id)) Visit(input.FromNodeId);
        }
        Visit(edge.FromNodeId);
        if (ancestors.Contains(matchingNodeId)) throw new InvalidDataException("上游图像连接存在循环。");
        var upstream = flow with { Nodes = flow.Nodes.Where(n => ancestors.Contains(n.Id)).ToList(),
            Edges = flow.Edges.Where(e => ancestors.Contains(e.FromNodeId) && ancestors.Contains(e.ToNodeId)).ToList(), DetectionPanels = [], InspectionTasks = [], InspectionRegions = [] };
        var prepared = PrepareNextImageFlow(upstream, out _, out _, out var ended);
        if (ended) prepared = PrepareNextImageFlow(upstream, out _, out _, out _);
        if (prepared is null) throw new InvalidOperationException("未能获取上游图像。");
        Log("模板示教：正在执行连接的上游节点。");
        var run = await RunSelectedFlowAsync(flowOverride: prepared);
        if (_shuttingDown || SelectedFlow?.Id != flow.Id) { run.Dispose(); throw new OperationCanceledException("模板示教已取消。"); }
        SetExecutionRun(run);
        if (run.Status == WorkflowRunStatus.Cancelled) throw new OperationCanceledException("模板示教已取消。");
        if (run.Status != WorkflowRunStatus.Success) throw new InvalidOperationException("上游图像执行失败：" + string.Join("；", run.Errors));
    }

    public async Task RunImageSourceAsync(Guid sourceId)
    {
        var flow = SelectedFlow?.Document ?? throw new InvalidOperationException("请先选择流程。");
        var ids = new HashSet<Guid>();
        void Visit(Guid id) { if (!ids.Add(id)) return; foreach (var edge in flow.Edges.Where(e => e.ToNodeId == id)) Visit(edge.FromNodeId); }
        Visit(sourceId);
        var upstream = flow with { Nodes = flow.Nodes.Where(n => ids.Contains(n.Id)).ToList(), Edges = flow.Edges.Where(e => ids.Contains(e.FromNodeId) && ids.Contains(e.ToNodeId)).ToList(),
            InspectionTasks = [], InspectionRegions = [], DetectionPanels = [] };
        var prepared = PrepareNextImageFlow(upstream, out _, out _, out var ended);
        if (ended) prepared = PrepareNextImageFlow(upstream, out _, out _, out _);
        var run = await RunSelectedFlowAsync(flowOverride: prepared ?? throw new InvalidDataException("无法获取区域来源图像。"));
        if (_shuttingDown || SelectedFlow?.Id != flow.Id) { run.Dispose(); throw new OperationCanceledException(); }
        SetExecutionRun(run);
        if (run.Status != WorkflowRunStatus.Success) throw new InvalidDataException("区域来源执行失败：" + string.Join("；", run.Errors));
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
        EnsureActive();
        if (IsRunning) throw new InvalidOperationException("流程已经在运行。");
        if (Workspace is null || SelectedFlow is null) throw new InvalidOperationException("请先打开项目和流程。");
        SelectedFlow.EnsureRoiEditsValid();
        using var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation; Raise(nameof(IsRunning)); Raise(nameof(RunStateText)); RefreshProductVerdict(); Raise(nameof(CanConfigureCamera));
        try
        {
            var document = flowOverride ?? SelectedFlow.Document;
            var token = cancellation.Token;
            var configuration = SelectedFlow.Document;
            _activeRunTask = Task.Run(() => _engine.RunAsync(document, progress, token, configuration));
            var run = await _activeRunTask;
            if (_shuttingDown) run.Dispose();
            return run;
        }
        finally { _activeRunTask = null; _runCancellation = null; Raise(nameof(IsRunning)); Raise(nameof(RunStateText)); RefreshProductVerdict(); Raise(nameof(CanConfigureCamera)); }
    }

    public void CancelRun() => _runCancellation?.Cancel();

    public void ReleaseModelSessions()
    {
        if (IsRunning) throw new InvalidOperationException("请等待当前推理结束后释放模型。");
        try { _dlSessions.Clear(); } finally { _matchModels.Clear(); }
    }
    public void SetExecutionRun(WorkflowExecutionRun run)
    {
        if (!ReferenceEquals(LastRun, run)) LastRun?.Dispose();
        LastRun = run;
        RefreshProductVerdict();
        ExecutionResults.Clear();
        foreach (var result in run.NodeResults) ExecutionResults.Add(result);
        Raise(nameof(ExecutionResults));
    }

    private void OpenWorkspace(ProjectWorkspace workspace)
    {
        EnsureActive();
        if (IsRunning || _cameraOperations.Count > 0) throw new InvalidOperationException("请等待运行或相机操作结束后切换项目。");
        var flows = _projectStore.ListFlows(workspace);
        if (flows.Count == 0) throw new InvalidDataException("项目中没有流程文件。");
        ReleaseModelSessions();
        LastRun?.Dispose();
        LastRun = null;
        ExecutionResults.Clear();
        DisconnectCamera();
        _imageSequenceCursors.Clear();
        var tabs = flows.Select(flow => new FlowTabViewModel(flow, _registry, _validator)).ToArray();
        Workspace = workspace; Flows.Clear(); foreach (var tab in tabs) Flows.Add(tab);
        SelectedFlow = tabs.FirstOrDefault(tab => tab.Id.ToString("N") == workspace.Project.DefaultFlowId) ?? tabs[0];
        RefreshProjectTree(); ProjectStatus = $"已打开项目：{workspace.Project.Name}";
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(_disposed || _shuttingDown, this);

    private async Task<T> CameraOperationAsync<T>(Func<T> action)
    {
        var task = Task.Run(action);
        _cameraOperations.Add(task);
        Raise(nameof(CanConfigureCamera));
        try { return await task; }
        finally { _cameraOperations.Remove(task); Raise(nameof(CanConfigureCamera)); }
    }

    public async Task WaitForCameraOperationsAsync()
    {
        while (_cameraOperations.Count > 0)
        {
            try { await Task.WhenAll(_cameraOperations.ToArray()); }
            catch { /* The initiating UI handler reports camera errors. */ }
            // Allow operation owners to run their finally blocks and remove completed tasks.
            await Task.Yield();
        }
    }

    public async Task DisconnectCameraAsync()
    {
        await WaitForCameraOperationsAsync();
        await CameraOperationAsync(() => { _cameraManager.Disconnect(); return true; });
        CameraStatus = "未连接";
        Raise(nameof(IsCameraConnected));
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _shuttingDown = true;
        CancelRun();
        if (_activeRunTask is { } task)
        {
            try { (await task).Dispose(); }
            catch { /* Run results and errors are handled by the execution owner. */ }
        }
        while (IsRunning) await Task.Delay(1);
        await DisconnectCameraAsync();
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (IsRunning || _cameraOperations.Count > 0)
            throw new InvalidOperationException("运行期间必须先调用 ShutdownAsync，等待资源使用者退出。");
        _disposed = true;
        LastRun?.Dispose();
        LastRun = null;
        ExecutionResults.Clear();
        _imageSequenceCursors.Clear();
        try { _cameraManager.Dispose(); }
        finally { try { _dlSessions.Dispose(); } finally { _matchModels.Dispose(); } }
    }
}
