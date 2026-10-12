using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Domain;
using HalconVision.Application;
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
        if (args.FirstOrDefault() == "--rectangle-line-check")
        {
            _root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/rectangle-line-check");
            Directory.CreateDirectory(_root);
            try { CheckRectangleLine(); File.WriteAllLines(Path.Combine(_root, "result.txt"), Evidence); }
            catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            foreach (var line in Evidence) Console.WriteLine(line);
            return;
        }
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

    private static void CheckRectangleLine()
    {
        var definition = new HalconNodeProvider().GetDefinitions().Single(item => item.TypeId == HalconNodeProvider.StraightLineFind);
        Assert(definition.Parameters.Any(item => item.Id == "roi" && item.DataType == "roiDraw"), "line node exposes a drawable rectangle search region");
        using var blank = new HImage(); blank.GenImageConst("byte", 256, 256);
        using var bright = new HRegion(); bright.GenRectangle1(0d, 120d, 255d, 255d);
        using var outsideEdge = new HRegion(); outsideEdge.GenRectangle1(0d, 30d, 255d, 50d);
        using var brightWithDistractor = bright.Union2(outsideEdge);
        using var stepImage = blank.PaintRegion(brightWithDistractor, 255d, "fill");
        var parameters = definition.Parameters.ToDictionary(item => item.Id, item => item.DefaultValue!.Value);
        parameters["roi"] = JsonSerializer.SerializeToElement("40,90,210,150");
        parameters["lineOrientation"] = JsonSerializer.SerializeToElement("vertical");
        parameters["measureThreshold"] = JsonSerializer.SerializeToElement(10d);
        parameters["showMeasureRegions"] = JsonSerializer.SerializeToElement(true);
        var node = new NodeInstance { TypeId = HalconNodeProvider.StraightLineFind, Parameters = parameters };
        var result = new HalconStraightLineFindExecutor().ExecuteAsync(new NodeExecutionContext(node,
            new Dictionary<string, JsonElement> { ["image"] = JsonSerializer.SerializeToElement(new { width = 256, height = 256, count = 1 }) },
            CancellationToken.None, _ => { }, new Dictionary<string, object?> { ["image"] = stepImage })).Result;
        try
        {
            var line = result.RuntimePorts!["line"] as Line2D;
            Assert(line is not null && Math.Abs(line.ColumnBegin - 120) < 2 && Math.Abs(line.ColumnEnd - 120) < 2, "rectangle detects the real vertical edge");
            Assert(((PointSet)result.RuntimePorts["edgePoints"]!).Points.All(point => point.Row >= 40 && point.Row <= 210 && point.Column >= 90 && point.Column <= 150),
                "strong distractor edges outside the rectangle do not enter the fit");
            var contour = (HXLDCont)result.RuntimePorts["lines"]!;
            contour.GetContourXld(out HTuple rows, out HTuple columns);
            using (rows) using (columns) Assert(rows.Length == 2 && contour.CountObj() == 1, "fitted output is exactly one straight segment with two endpoints");
            var preview = (HImage)result.RuntimePorts["preview"]!;
            using var red = preview.AccessChannel(1);
            using var green = preview.AccessChannel(2);
            using var diagnosticBlue = preview.AccessChannel(3);
            using var diagnosticPixel = diagnosticBlue.GetGrayval(45, 100);
            Assert(diagnosticPixel.D == 0, "legacy caliper overlays stay hidden until diagnostic display is explicitly enabled");
            using var borderRed = red.GetGrayval(40, 100);
            using var borderGreen = green.GetGrayval(40, 100);
            Assert(borderRed.D == 255 && borderGreen.D == 220, "search rectangle boundary is painted in yellow");
            Assert(line is not null && Math.Abs(line.RowBegin - 40) < 0.01 && Math.Abs(line.RowEnd - 210) < 0.01,
                "fitted vertical line extends to both ends of the search rectangle");
            using var redValue = red.GetGrayval(125, 118);
            using var greenValue = green.GetGrayval(125, 118);
            Assert(redValue.D == 255 && greenValue.D == 0, "final red fitted line is visibly thick rather than a one-pixel contour");
            preview.WriteImage("png", 0, Path.Combine(_root, "rectangle-line.png"));
        }
        finally
        {
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var value in result.RuntimePorts!.Values)
                if (value is IDisposable disposable && released.Add(value)) disposable.Dispose();
        }
        using var horizontalBright = new HRegion(); horizontalBright.GenRectangle1(120d, 0d, 255d, 255d);
        using var horizontalImage = blank.PaintRegion(horizontalBright, 255d, "fill");
        parameters["roi"] = JsonSerializer.SerializeToElement("90,40,150,210");
        parameters["lineOrientation"] = JsonSerializer.SerializeToElement("horizontal");
        parameters["showDiagnostics"] = JsonSerializer.SerializeToElement(true);
        var horizontalResult = new HalconStraightLineFindExecutor().ExecuteAsync(new NodeExecutionContext(node,
            new Dictionary<string, JsonElement> { ["image"] = JsonSerializer.SerializeToElement(new { width = 256, height = 256, count = 1 }) },
            CancellationToken.None, _ => { }, new Dictionary<string, object?> { ["image"] = horizontalImage })).Result;
        try
        {
            var line = horizontalResult.RuntimePorts!["line"] as Line2D;
            Assert(line is not null && Math.Abs(line.RowBegin - 120) < 2 && Math.Abs(line.RowEnd - 120) < 2, "horizontal direction detects a horizontal line inside the rectangle");
            Assert(line is not null && Math.Abs(Math.Min(line.ColumnBegin, line.ColumnEnd) - 40) < 0.01 &&
                Math.Abs(Math.Max(line.ColumnBegin, line.ColumnEnd) - 210) < 0.01,
                "fitted horizontal line extends to both sides of the search rectangle");
            using var diagnosticBlue = ((HImage)horizontalResult.RuntimePorts!["preview"]!).AccessChannel(3);
            using var diagnosticPixel = diagnosticBlue.GetGrayval(100, 45);
            Assert(diagnosticPixel.D == 255, "explicit diagnostic display still exposes measurement calipers");
        }
        finally
        {
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var value in horizontalResult.RuntimePorts!.Values)
                if (value is IDisposable disposable && released.Add(value)) disposable.Dispose();
        }
    }

    private static async Task CheckExternalProjectAsync(MainWindow window, string path)
    {
        var vm = (WorkflowWorkspaceViewModel)window.DataContext;
        Assert(window.FindName("DetectionSummary") is TextBlock,
            "image panel has a visible detection-result summary");
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
        var lineParameter = detectorView.Parameters.Single(parameter => parameter.IsRoiDraw);
        flow.SelectNode(detector.Id, false);
        window.UpdateLayout();
        Assert(Descendants(window).OfType<Button>().Any(button => button.IsVisible && button.Content?.ToString() == "绘制矩形范围") &&
            !Descendants(window).OfType<Button>().Any(button => button.Content?.ToString() == "绘制参考线"),
            "line-find property editor offers only rectangle drawing, not reference-line drawing");
        typeof(MainWindow).GetMethod("DrawRoi_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new Button { Tag = lineParameter }, new RoutedEventArgs()]);
        Assert(Field<Canvas>(window, "RoiDrawingOverlay").Visibility == Visibility.Visible,
            "1900 draw-rectangle action displays the source image and activates the drawing overlay");
        // Older saved projects can still contain only referenceLine. The test
        // draws the known casing rectangle in memory without saving the project.
        lineParameter.ValueText = detector.Parameters.TryGetValue("roi", out var roiValue)
            ? roiValue.GetString()! : "417,1978,1575,2415";
        lineParameter.Commit();
        typeof(MainWindow).GetMethod("FinishRoiDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
        Assert(Field<Canvas>(window, "RoiDrawingOverlay").Visibility == Visibility.Collapsed &&
            Field<Rectangle>(window, "RoiPreviewRectangle").Visibility == Visibility.Collapsed,
            "1900 drawing overlay is removed when drawing ends");
        Assert(lineParameter.Error is null, "1900 rectangle editor commits image coordinates as a string parameter");
        flow.SetParameter(source.Id, "path", JsonSerializer.SerializeToElement(firstImage));
        flow.SetParameter(detector.Id, "measureThreshold", JsonSerializer.SerializeToElement(5d));
        flow.SetParameter(detector.Id, "minScore", JsonSerializer.SerializeToElement(0.2d));
        var actualNode = flow.Document.Nodes.Single(node => node.Id == detector.Id);
        var imageMetadata = JsonSerializer.SerializeToElement(new { count = 1, width, height, channels = 3 });
        var directExecution = Task.Run(() => new HalconStraightLineFindExecutor().ExecuteAsync(new NodeExecutionContext(
            actualNode, new Dictionary<string, JsonElement> { ["image"] = imageMetadata }, CancellationToken.None,
            message => { Console.WriteLine(message); Console.Out.Flush(); }, new Dictionary<string, object?> { ["image"] = image })).AsTask());
        var directCompleted = await Task.WhenAny(directExecution, Task.Delay(TimeSpan.FromSeconds(30))) == directExecution;
        Assert(directCompleted, "1900 direct 2D Metrology execution completes within 30 seconds");
        var directOutput = await directExecution;
        var directStatus = (LineFindStatus)directOutput.RuntimePorts!["status"]!;
        Assert(directStatus.Code == LineFindStatus.Found, "1900 casing rectangle detects a line at threshold 5 and minimum score 0.2");
        Evidence.Add($"DETAIL: direct status={directStatus.Code}; line={directOutput.RuntimePorts["line"]}; edges={(directOutput.RuntimePorts["edgePoints"] as PointSet)?.Points.Count}");
        var directDisposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var value in directOutput.RuntimePorts.Values)
            if (value is IDisposable disposable && directDisposed.Add(value)) disposable.Dispose();
        using var blank = new HImage();
        blank.GenImageConst("byte", 100, 100);
        var noLineNode = actualNode with { Parameters = new Dictionary<string, JsonElement>
            { ["roi"] = JsonSerializer.SerializeToElement("10,10,90,90"), ["lineOrientation"] = JsonSerializer.SerializeToElement("horizontal") } };
        var noLineOutput = await Task.Run(() => new HalconStraightLineFindExecutor().ExecuteAsync(new NodeExecutionContext(
            noLineNode, new Dictionary<string, JsonElement> { ["image"] = JsonSerializer.SerializeToElement(new { count = 1, width = 100, height = 100, channels = 1 }) },
            CancellationToken.None, _ => { }, new Dictionary<string, object?> { ["image"] = blank })).AsTask());
        Assert(noLineOutput.RuntimePorts!["line"] is null && noLineOutput.RuntimePorts["status"] is LineFindStatus { Code: LineFindStatus.NotFound },
            "blank image returns NOT_FOUND and an empty geometry output instead of a zero-coordinate line");
        var noLineDisposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var value in noLineOutput.RuntimePorts.Values)
            if (value is IDisposable disposable && noLineDisposed.Add(value)) disposable.Dispose();
        flow.SetParameter(detector.Id, "measureThreshold", JsonSerializer.SerializeToElement(5d));
        flow.SetParameter(detector.Id, "minScore", JsonSerializer.SerializeToElement(0.2d));
        Assert(flow.ValidationIssues.Count == 0, "1900 flow validation refreshes after ROI coordinates are committed");
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
        using (var channels = ((HImage)resultValue!).CountChannels())
            Assert(channels.I == 3, "1900 annotated result is RGB so detected lines remain visible on bright grayscale edges");
        ((HImage)resultValue!).WriteImage("png", 0, Path.Combine(_root, "1900-line-result.png"));
        Assert(outputs.TryGetValue("lines", out var linesValue) && linesValue is HXLDCont lines && lines.IsInitialized() && lines.CountObj() > 0,
            "1900 2D Metrology line-find returns non-empty fitted-line XLD contours");
        var fittedContour = (HXLDCont)outputs["lines"]!;
        fittedContour.GetContourXld(out HTuple fittedRows, out HTuple fittedColumns);
        using (fittedRows) using (fittedColumns)
            Assert(fittedContour.CountObj() == 1 && fittedRows.Length == 2, "1900 output is exactly one fitted straight line");
        var searchOverlay = (GeometryOverlay)outputs["overlay"]!;
        var fittedLine = (Line2D)outputs["line"]!;
        using (var fitRed = ((HImage)resultValue!).AccessChannel(1))
        using (var fitGreen = ((HImage)resultValue!).AccessChannel(2))
        using (var redPixel = fitRed.GetGrayval((int)Math.Round((fittedLine.RowBegin + fittedLine.RowEnd) / 2),
            (int)Math.Round((fittedLine.ColumnBegin + fittedLine.ColumnEnd) / 2) + 5))
        using (var greenPixel = fitGreen.GetGrayval((int)Math.Round((fittedLine.RowBegin + fittedLine.RowEnd) / 2),
            (int)Math.Round((fittedLine.ColumnBegin + fittedLine.ColumnEnd) / 2) + 5))
            Assert(redPixel.D == 255 && greenPixel.D == 0, "1900 full-length red line has visible thickness even in the reduced image panel");
        Assert(searchOverlay.SearchRegion is { } frame &&
            Math.Abs(Math.Min(fittedLine.RowBegin, fittedLine.RowEnd) - frame.RowBegin) < 0.01 &&
            Math.Abs(Math.Max(fittedLine.RowBegin, fittedLine.RowEnd) - frame.RowEnd) < 0.01,
            "1900 fitted line spans the complete rectangle height");
        using (var borderRed = ((HImage)resultValue!).AccessChannel(1))
        using (var borderGreen = ((HImage)resultValue!).AccessChannel(2))
        using (var redPixel = borderRed.GetGrayval((int)Math.Round(searchOverlay.SearchRegion!.RowBegin) + 4,
            (int)Math.Round((3 * searchOverlay.SearchRegion.ColumnBegin + searchOverlay.SearchRegion.ColumnEnd) / 4)))
        using (var greenPixel = borderGreen.GetGrayval((int)Math.Round(searchOverlay.SearchRegion.RowBegin) + 4,
            (int)Math.Round((3 * searchOverlay.SearchRegion.ColumnBegin + searchOverlay.SearchRegion.ColumnEnd) / 4)))
            Assert(redPixel.D == 255 && greenPixel.D == 220, "1900 rectangle border stays thick and yellow above caliper overlays");
        Assert(searchOverlay.SearchRegion is not null && searchOverlay.EdgePoints.Points.All(point =>
            point.Row >= searchOverlay.SearchRegion.RowBegin && point.Row <= searchOverlay.SearchRegion.RowEnd &&
            point.Column >= searchOverlay.SearchRegion.ColumnBegin && point.Column <= searchOverlay.SearchRegion.ColumnEnd),
            "1900 fitted edge samples are restricted to the drawn rectangle");
        Assert(outputs.TryGetValue("status", out var statusValue) && statusValue is LineFindStatus { Code: LineFindStatus.Found },
            "1900 2D Metrology emits structured FOUND status");
        Assert(outputs.TryGetValue("edgePoints", out var pointsValue) && pointsValue is PointSet { Points.Count: > 0 },
            "1900 2D Metrology returns actual fitted edge sample points");
        Assert(((LineFindStatus)outputs["status"]!).Code == LineFindStatus.Found,
            "1900 legacy ROI migrates to a reference line and finds the real casing edge");
        Assert(Field<TextBlock>(window, "PreviewEmptyText").Visibility == Visibility.Collapsed && flow.SelectedNode?.Id == detector.Id,
            "1900 final node result is selected and shown in the image preview");
        Assert(Field<TextBlock>(window, "DetectionSummary").Text.Contains("找到直线") &&
            Field<TextBlock>(window, "DetectionSummary").Text.Contains("评分"),
            "image panel visibly distinguishes a found line and displays its score");
        SaveWindow(window, "found-ui.png");
        SavePreview(window);
        var count = outputs["lines"] is HXLDCont contours ? contours.CountObj() : -1;
        Evidence.Add($"DETAIL: 1900 source={Path.GetFileName(firstImage)}; size={width}x{height}; lines={count}; project not saved");
        var retainedObjects = Objects(run).ToArray();
        Assert(NodeResultSummary.Describe(run.NodeResults.Single(result => result.NodeId == detector.Id)).StartsWith("找到直线"),
            "result table displays FOUND instead of generic execution success");
        var blankPath = Path.Combine(_root, "blank.png");
        blank.WriteImage("png", 0, blankPath);
        flow.SetParameter(source.Id, "path", JsonSerializer.SerializeToElement(blankPath));
        flow.SetParameter(detector.Id, "roi", JsonSerializer.SerializeToElement("10,10,90,90"));
        Invoke(window, "Run_Click");
        await UntilAsync(() => ExecutionTask(window) is null);
        Assert(Field<TextBlock>(window, "DetectionSummary").Text.StartsWith("未找到直线") &&
            NodeResultSummary.Describe(vm.LastRun!.NodeResults.Last()).StartsWith("未找到直线"),
            "blank-image run displays NOT_FOUND in both image panel and result table");
        SaveWindow(window, "not-found-ui.png");
        Assert(retainedObjects.All(value => !value.IsInitialized()), "rerun releases previous line-find images and contours");
        retainedObjects = Objects(vm.LastRun!).ToArray();
        flow.MarkSaved(); // In-memory test edits are disposable; do not save the user's project.
        window.Close();
        await UntilAsync(() => !window.IsVisible);
        Assert(retainedObjects.All(value => !value.IsInitialized()), "workspace shutdown releases retained HALCON images and contours");
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

    private static void SaveWindow(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,
            (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_root, name));
        encoder.Save(file);
    }

    private static void SavePreview(Window window)
    {
        var panel = (FrameworkElement)VisualTreeHelper.GetParent(Field<Grid>(window, "PreviewHost"));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)panel.ActualWidth,
            (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_root, "found-preview.png"));
        encoder.Save(file);
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
