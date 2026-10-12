using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

// Runs real WPF event handlers and HALCON operators. Acquisition lifecycle uses
// HALCON's official File framegrabber, injected only in this test executable.
internal static class Program
{
    private static readonly List<string> Evidence = [];
    private static long _heartbeatAt = Environment.TickCount64;
    private static int _ticks;
    private static string _root = "";
    private static System.Windows.Application _app = null!;
    private static volatile bool _finished;
    private static volatile bool _watchdogEnabled;

    [STAThread]
    private static void Main(string[] args)
    {
        _root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/lifecycle-checks");
        Directory.CreateDirectory(_root);
        var externalProject = args.ElementAtOrDefault(1);
        var path = externalProject is null ? CreateFixture() : Path.GetFullPath(externalProject);
        _app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _app.DispatcherUnhandledException += (_, e) => Fail(e.Exception);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) =>
        {
            Interlocked.Increment(ref _ticks);
            Interlocked.Exchange(ref _heartbeatAt, Environment.TickCount64);
        }, _app.Dispatcher);
        _ = Task.Run(async () =>
        {
            while (!_finished)
            {
                await Task.Delay(200);
                if (_watchdogEnabled && Environment.TickCount64 - Interlocked.Read(ref _heartbeatAt) > 2000)
                    Fail(new InvalidOperationException($"WPF dispatcher unresponsive for >2 seconds; heartbeat={_ticks}"));
            }
        });
        var window = new MainWindow(path);
        _app.MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(100);
                Interlocked.Exchange(ref _heartbeatAt, Environment.TickCount64);
                _watchdogEnabled = true;
                if (externalProject is null) await CheckAsync(window, path);
                else await CheckExternalProjectAsync(window, path);
                _finished = true;
                File.WriteAllLines(Path.Combine(_root, "result.txt"), Evidence);
                foreach (var line in Evidence) Console.WriteLine(line);
                _app.Shutdown();
            }
            catch (Exception ex) { Fail(ex); }
        };
        window.Show();
        _app.Run();
    }

    private static async Task CheckExternalProjectAsync(MainWindow window, string path)
    {
        var vm = (WorkflowWorkspaceViewModel)window.DataContext;
        var flow = vm.SelectedFlow!;
        var source = flow.Document.Nodes.Single(node => node.TypeId == HalconNodeProvider.ImageRead);
        var detector = flow.Document.Nodes.Single(node => node.TypeId == HalconNodeProvider.StraightLineFind);
        Assert(flow.Document.Edges.Any(edge => edge.FromNodeId == source.Id && edge.FromPortId == "image" &&
            edge.ToNodeId == detector.Id && edge.ToPortId == "image"), "1900 image-read node is wired to straight-line-find");
        var folder = source.Parameters["path"].GetString()!;
        var firstImage = Directory.EnumerateFiles(folder).Where(file =>
            new[] { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".jp2" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).First();
        using var image = new HImage(firstImage);
        image.GetImageSize(out int width, out int height);
        var detectorView = flow.Nodes.Single(node => node.Id == detector.Id);
        var roiParameter = detectorView.Parameters.Single(parameter => parameter.IsRoiDraw);
        flow.SelectNode(detector.Id, false);
        typeof(MainWindow).GetMethod("DrawRoi_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new Button { Tag = roiParameter }, new RoutedEventArgs()]);
        Assert(Field<Canvas>(window, "RoiDrawingOverlay").Visibility == Visibility.Visible,
            "1900 draw-range action displays the source image and activates the ROI overlay");
        typeof(MainWindow).GetMethod("FinishRoiDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
        roiParameter.ValueText = $"{height / 10},{width / 10},{height * 3 / 10},{width * 3 / 10}";
        roiParameter.Commit();
        Assert(roiParameter.Error is null, "1900 ROI editor commits drawn coordinates as a string parameter");
        Invoke(window, "Run_Click");
        await UntilAsync(() => ExecutionTask(window) is null, TimeSpan.FromMinutes(2));
        var run = vm.LastRun ?? throw new InvalidOperationException("1900 run did not produce a result.");
        Evidence.Add($"DETAIL: status={run.Status}; nodes={string.Join(" | ", run.NodeResults.Select(result => $"{result.Status}: {result.Error}"))}; errors={string.Join(" | ", run.Errors)}");
        Assert(run.Status == WorkflowRunStatus.Success && run.NodeResults.Count == 2 &&
            run.NodeResults.All(result => result.Status == NodeExecutionStatus.Success),
            "1900 image read and line-find nodes both execute successfully");
        var outputs = run.RuntimeOutputs![detector.Id];
        Assert(outputs.TryGetValue("resultImage", out var resultValue) && resultValue is HImage resultImage && resultImage.IsInitialized(),
            "1900 line-find returns an initialized annotated HImage");
        Assert(outputs.TryGetValue("lines", out var linesValue) && linesValue is HXLDCont lines && lines.IsInitialized() && lines.CountObj() > 0,
            "1900 line-find returns non-empty XLD contours");
        Assert(Field<TextBlock>(window, "PreviewEmptyText").Visibility == Visibility.Collapsed && flow.SelectedNode?.Id == detector.Id,
            "1900 final node result is selected and shown in the image preview");
        var count = outputs["lines"] is HXLDCont contours ? contours.CountObj() : -1;
        Evidence.Add($"DETAIL: 1900 source={Path.GetFileName(firstImage)}; size={width}x{height}; lines={count}; project not saved");
    }

    private static string CreateFixture()
    {
        var folder = Path.Combine(_root, "images");
        Directory.CreateDirectory(folder);
        using var black = new HImage();
        black.GenImageConst("byte", 64, 64);
        black.WriteImage("png", 0, Path.Combine(folder, "01.png"));
        using var white = black.ScaleImage(0d, 255d);
        white.WriteImage("png", 0, Path.Combine(folder, "02.png"));
        var input = new NodeInstance { TypeId = HalconNodeProvider.ImageRead, DisplayName = "读取图像", X = 50, Y = 70,
            Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(folder) } };
        var threshold = new NodeInstance { TypeId = HalconNodeProvider.Threshold, DisplayName = "二值化", X = 250, Y = 90,
            Parameters = new() { ["minimum"] = JsonSerializer.SerializeToElement(128d), ["maximum"] = JsonSerializer.SerializeToElement(255d) } };
        return SaveFixture("检查项目", input, threshold);
    }

    private static string SaveFixture(string name, NodeInstance input, NodeInstance threshold)
    {
        var flow = new FlowDocument { Name = "生命周期回归", Nodes = [input, threshold],
            Edges = [new FlowEdge(Guid.NewGuid(), input.Id, "image", threshold.Id, "image")] };
        var path = Path.Combine(_root, name + ".hvproj");
        var project = new ProjectDocument { Name = name, Flows = [flow], DefaultFlowId = flow.Id.ToString("N") };
        new ProjectStore().SaveProject(new ProjectWorkspace(_root, project, path));
        return path;
    }

    private static async Task CheckAsync(MainWindow window, string path)
    {
        var vm = (WorkflowWorkspaceViewModel)window.DataContext;
        var flow = vm.SelectedFlow!;
        Assert(vm.Workspace?.ProjectFilePath == path && flow.Nodes.Count == 2 && flow.Edges.Count == 1,
            "startup project argument loads nodes and wiring without manual reopen");
        var nodeCanvas = Descendants(window).OfType<ItemsControl>()
            .Single(control => ReferenceEquals(control.ItemsSource, flow.Nodes));
        Assert(nodeCanvas.Items.Count == 2 && nodeCanvas.ItemContainerGenerator.ContainerFromIndex(1) is not null,
            "startup selected flow renders both node containers");
        var resolve = typeof(App).GetMethod("ResolveProjectArgument", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var arguments in new[] { new[] { path }, new[] { "--project", path },
            new[] { "--project=" + path }, new[] { new Uri(path).AbsoluteUri } })
            Assert((string?)resolve.Invoke(null, [arguments]) == path, "startup argument format: " + arguments[0]);
        flow.SelectNode(flow.Nodes[0].Id, false);
        flow.CopySelection(); flow.PasteSelection();
        Assert(flow.Nodes.Count == 3, "node copy/paste");
        Invoke(window, "Delete_Click");
        Assert(flow.Nodes.Count == 2 && flow.Edges.Count == 1, "delete selected pasted node");
        Invoke(window, "Undo_Click"); Assert(flow.Nodes.Count == 3, "undo delete");
        Invoke(window, "Redo_Click"); Assert(flow.Nodes.Count == 2, "redo delete");
        var inputId = flow.Nodes[0].Id;
        var thresholdId = flow.Nodes[1].Id;
        flow.BeginMove(); flow.MoveNode(inputId, 110, 125);
        Assert(flow.Nodes[0].X == 110 && flow.Nodes[0].Y == 125, "node move");
        flow.SetZoom(.4); Assert(Math.Abs(flow.Zoom - .4) < .001, "canvas zoom");
        Assert(!flow.Connect(inputId, "image", thresholdId, "image", out _), "reject duplicate wire");
        flow.SelectNode(thresholdId, false); Invoke(window, "Delete_Click");
        Assert(flow.Nodes.Count == 1 && flow.Edges.Count == 0, "delete node removes edges");
        Invoke(window, "Undo_Click"); Assert(flow.Nodes.Count == 2 && flow.Edges.Count == 1, "undo restores wiring");
        vm.SaveAll(); vm.OpenProject(path);
        Assert(vm.SelectedFlow!.Nodes.Count == 2 && vm.SelectedFlow.Edges.Count == 1, "project save/reopen");
        var savedNode = vm.SelectedFlow.Document.Nodes[0];
        vm.SelectedFlow.SetParameter(savedNode.Id, "path", savedNode.Parameters["path"]);
        Assert(!vm.SelectedFlow.IsDirty, "unchanged parameter does not create unsaved changes");
        await RunOnceAsync(window);
        var first = vm.LastRun!;
        Assert(first.Status == WorkflowRunStatus.Success, "single run threshold succeeds");
        Assert(Source(first).EndsWith("01.png"), "folder single run first image");
        Assert(RegionArea(first) == 0, "threshold black input produces zero foreground area");
        var firstObjects = Objects(first);
        await RunOnceAsync(window);
        Assert(Source(vm.LastRun!).EndsWith("02.png"), "folder single run next image");
        Assert(RegionArea(vm.LastRun!) == 4096, "threshold white input produces 64x64 foreground area");
        Assert(firstObjects.All(value => !value.IsInitialized()), "previous HALCON outputs disposed on replacement");
        var completed = 0;
        var released = 0;
        var stressStart = Stopwatch.StartNew();
        while (completed < 200)
        {
            // A synthetic batch of button invocations must allow WPF to process
            // its timer/render queue between clicks, as real user input does.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var previous = vm.LastRun!;
            var owned = Objects(previous);
            await RunOnceAsync(window);
            if (ReferenceEquals(previous, vm.LastRun)) continue; // Single-step folder wrap resets without processing.
            if (owned.Any(value => value.IsInitialized())) throw new InvalidOperationException("Retained native output in repeated execution.");
            released += owned.Length;
            completed++;
        }
        Assert(released == 400, "200 real HALCON runs release 400 previous image/region objects");
        Evidence.Add($"METRIC: repeated executions={completed}; released objects={released}; elapsed={stressStart.ElapsedMilliseconds} ms");

        var heartbeatBefore = _ticks;
        Invoke(window, "ContinuousRun_Click");
        var execution = ExecutionTask(window);
        Invoke(window, "Run_Click"); Invoke(window, "ContinuousRun_Click");
        Assert(ReferenceEquals(execution, ExecutionTask(window)), "prevent run reentry");
        await Task.Delay(2000);
        Assert(_ticks - heartbeatBefore >= 20, "continuous dispatcher responsiveness");
        var startStop = Stopwatch.StartNew();
        Invoke(window, "CancelRun_Click"); await UntilAsync(() => ExecutionTask(window) is null);
        Assert(!vm.IsRunning && !vm.IsContinuousRunning, "stop drains execution task");
        Evidence.Add($"METRIC: heartbeat={_ticks - heartbeatBefore}; stop={startStop.ElapsedMilliseconds} ms");
        for (var i = 0; i < 10; i++)
        {
            Invoke(window, "ContinuousRun_Click"); await Task.Delay(90);
            Invoke(window, "CancelRun_Click"); await UntilAsync(() => ExecutionTask(window) is null);
        }
        Assert(!vm.IsRunning, "10 repeated start/stop cycles");
        for (var i = 0; i < 700; i++) vm.Log("bounded log check " + i);
        Assert(vm.Logs.Count == WorkflowWorkspaceViewModel.MaximumLogEntries, "bounded UI logs");
        var oldOutputs = Objects(vm.LastRun!);
        Invoke(window, "ContinuousRun_Click"); await Task.Delay(90);
        await (Task)Invoke(window, "PrepareProjectSwitchAsync", false)!;
        vm.OpenProject(path);
        Assert(oldOutputs.All(value => !value.IsInitialized()) && vm.LastRun is null, "project switch releases old results");

        var camera = new NodeInstance { TypeId = HalconNodeProvider.CameraCapture, DisplayName = "相机采集", X = 50, Y = 70,
            Parameters = new() { ["interface"] = JsonSerializer.SerializeToElement("File"), ["device"] = JsonSerializer.SerializeToElement("test-camera") } };
        var cameraPath = SaveFixture("相机生命周期检查", camera, vm.SelectedFlow!.Document.Nodes[1]);
        vm.OpenProject(cameraPath);
        var manager = Field<HalconCameraManager>(vm, "_cameraManager");
        var grabber = AttachFileCamera(manager);
        Invoke(window, "ContinuousRun_Click"); await Task.Delay(450);
        Assert(vm.LastRun?.Status == WorkflowRunStatus.Success && vm.LastRun.NodeResults.Count == 2, "camera-only continuous acquisition and threshold");
        Invoke(window, "CancelRun_Click"); await UntilAsync(() => ExecutionTask(window) is null);
        Assert(!vm.IsCameraConnected && Throws(() => grabber.GetFramegrabberParam("revision")), "stop closes native acquisition handle");
        var nativeObjects = Objects(vm.LastRun!);
        var viewer = new ImageViewerWindow(Path.Combine(_root, "images", "01.png")) { Owner = window };
        viewer.Show();
        AttachFileCamera(manager);
        Invoke(window, "ContinuousRun_Click"); await Task.Delay(100);
        window.Close(); await UntilAsync(() => !window.IsVisible);
        Assert(!vm.IsRunning && !vm.IsCameraConnected && vm.LastRun is null && nativeObjects.All(value => !value.IsInitialized()), "close while running drains tasks and releases HALCON objects");
        Assert(!viewer.IsVisible, "owned image viewer closes with workspace");
        vm.Dispose(); Assert(Throws(() => vm.OpenProject(path)), "disposed workspace rejects reuse");

        var counted = new CountingDisposable();
        var run = new WorkflowExecutionRun(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow,
            WorkflowRunStatus.Success, [], [], [], new Dictionary<Guid, IReadOnlyDictionary<string, object?>>
            { [Guid.NewGuid()] = new Dictionary<string, object?> { ["a"] = counted, ["b"] = counted } });
        run.Dispose(); run.Dispose(); Assert(counted.Count == 1, "aliased runtime output disposed exactly once");
        for (var i = 0; i < 3; i++)
        {
            _watchdogEnabled = false;
            var reopened = new MainWindow(path); reopened.Show(); await Task.Delay(50); reopened.Close();
            await UntilAsync(() => !reopened.IsVisible);
            Interlocked.Exchange(ref _heartbeatAt, Environment.TickCount64);
            _watchdogEnabled = true;
        }
        Assert(true, "3 workspace reopen/close cycles");
        var scanningWorkspace = new WorkflowWorkspaceViewModel();
        var scanning = scanningWorkspace.ScanCamerasAsync();
        var shutdown = scanningWorkspace.ShutdownAsync();
        await Task.WhenAll(scanning, shutdown);
        Assert(!scanningWorkspace.IsCameraConnected && Field<HashSet<Task>>(scanningWorkspace, "_cameraOperations").Count == 0,
            "shutdown waits for concurrent device scan");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static HFramegrabber AttachFileCamera(HalconCameraManager manager)
    {
        var grabber = new HFramegrabber("File", -1, -1, -1, -1, -1, -1, "default", -1, "default", -1,
            "default", Path.Combine(_root, "images", "01.png"), "default", -1, -1);
        SetField(manager, "_framegrabber", grabber); SetField(manager, "_connectedInterfaceId", "File"); SetField(manager, "_connectedDeviceId", "test-camera");
        return grabber;
    }
    private static async Task RunOnceAsync(MainWindow window)
    {
        Invoke(window, "Run_Click"); await UntilAsync(() => ExecutionTask(window) is null);
    }
    private static Task? ExecutionTask(MainWindow window) => Field<Task?>(window, "_executionTask");
    private static HObject[] Objects(WorkflowExecutionRun run) => run.RuntimeOutputs!.Values.SelectMany(value => value.Values).OfType<HObject>().ToArray();
    private static string Source(WorkflowExecutionRun run) => run.NodeResults[0].Outputs["image"].GetProperty("source").GetString()!;
    private static double RegionArea(WorkflowExecutionRun run)
    {
        var region = run.RuntimeOutputs!.Values.SelectMany(value => value.Values).OfType<HRegion>().Single();
        using var area = region.AreaCenter(out HTuple row, out HTuple column);
        using (row) using (column) return area.D;
    }
    private static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Lifecycle transition exceeded 5 seconds."); await Task.Delay(10); }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(MainWindow window, string name, bool eventHandler = true) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, eventHandler ? [window, new RoutedEventArgs()] : []);
    private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Evidence.Add("PASS: " + description);
    }
    private static void Fail(Exception exception)
    {
        _finished = true;
        var lines = Evidence.Append("FAIL: " + exception).ToArray();
        File.WriteAllLines(Path.Combine(_root, "result.txt"), lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        Process.GetCurrentProcess().Kill();
    }
    private sealed class CountingDisposable : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }
}
