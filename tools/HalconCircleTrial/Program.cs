using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    private static readonly List<string> Checks = [];
    private static readonly List<object> Measurements = [];
    private static string _root = "";
    private static readonly HalconCircleNodeProvider Provider = new();
    private static readonly NodeDefinition Definition = Provider.GetDefinitions().Single();
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0]) || File.Exists(args[0])) return 2;
        _root = Path.GetFullPath(args[0]); Directory.CreateDirectory(_root);
        try { Core(); Ui(); Save(); Console.WriteLine($"PASS {Checks.Count} 项圆查找检查"); return 0; }
        catch (Exception error) { Save(); File.WriteAllText(Path.Combine(_root, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Checks.Add(label); }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static NodeInstance Node() => new() { TypeId = HalconCircleNodeProvider.CircleFind, DisplayName = "圆查找", X = 320, Y = 100, Parameters = Definition.Parameters.ToDictionary(p => p.Id, p => p.DefaultValue!.Value) };
    private static NodeInstance Set(NodeInstance node, string key, object value) => node with { Parameters = new(node.Parameters) { [key] = J(value) } };
    private static NodeExecutionOutput Run(HImage image, NodeInstance node, CancellationToken token = default, Action<string>? log = null) =>
        new HalconCircleFindExecutor().ExecuteAsync(new(node, new Dictionary<string, JsonElement>(), token, log ?? (_ => { }), new Dictionary<string, object?> { ["image"] = image })).GetAwaiter().GetResult();
    private static void Release(NodeExecutionOutput output)
    {
        var images = output.RuntimePorts!.Values.OfType<HObject>().Distinct().ToArray();
        foreach (var obj in output.RuntimePorts.Values.OfType<IDisposable>().Distinct<IDisposable>(ReferenceEqualityComparer.Instance)) obj.Dispose();
        Check(images.All(i => !i.IsInitialized()), "本次预览对象已释放");
    }
    private static HImage Disk(double row, double column, double radius, bool inverse = false)
    {
        using var baseImage = new HImage(); baseImage.GenImageConst("byte", 512, 384);
        using var background = baseImage.ScaleImage(0d, inverse ? 220d : 30d);
        using var region = new HRegion(); region.GenCircle(row, column, radius);
        using var painted = background.PaintRegion(region, inverse ? 30d : 220d, "fill");
        return painted.GaussFilter(3);
    }
    private static void Found(NodeExecutionOutput output, double row, double column, double radius, double tolerance, string label)
    {
        var status = output.Ports["status"].Deserialize<CircleFindStatus>()!;
        var circle = output.RuntimePorts!["circle"] as Circle2D;
        Measurements.Add(new { label, status, circle, score = output.Ports["score"], residual = output.Ports["residualRms"] });
        Check(status.Code == CircleFindStatus.Found && circle is not null && output.Ports["count"].GetInt32() == 1, label + "：实际找到圆");
        Check(Math.Abs(circle!.Row - row) < tolerance && Math.Abs(circle.Column - column) < tolerance && Math.Abs(circle.Radius - radius) < tolerance, label + "：圆心和半径误差在范围内");
        Check(Math.Abs(output.Ports["diameter"].GetDouble() - circle.Diameter) < 1e-9 && output.Ports["edgePoints"].Deserialize<PointSet>()!.Points.Count >= 3, label + "：直径及实际边缘点输出完整");
    }
    private static void NotFound(NodeExecutionOutput output, string label)
    {
        Check(output.Ports["status"].Deserialize<CircleFindStatus>()!.Code == CircleFindStatus.NotFound && output.Ports["count"].GetInt32() == 0, label + "：明确未找到");
        Check(output.Ports["circle"].ValueKind == JsonValueKind.Null && output.Ports["diameter"].ValueKind == JsonValueKind.Null && output.RuntimePorts!["circle"] is null, label + "：不输出全零假圆或残留几何");
    }
    private static void Reject(HImage image, NodeInstance node, string label)
    {
        var rejected = false;
        try { var unexpected = Run(image, node); Release(unexpected); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }
    private static void Core()
    {
        using var image = Disk(173, 271, 65);
        var node = Set(Node(), "roi", "97,195,249,347");
        // gen_circle shifts integer centers to half pixels for integer radii to keep raster symmetry.
        using var truth = new HRegion(); truth.GenCircle(173d,271d,65d);
        using var truthArea = truth.AreaCenter(out HTuple truthRows, out HTuple truthColumns);
        using var truthRowScope = truthRows; using var truthColumnScope = truthColumns;
        Check(Math.Abs(truthRows.D-172.5)<1e-9 && Math.Abs(truthColumns.D-270.5)<1e-9, "合成圆基准使用实际栅格中心，避免把半像素生成规则误当测量误差");
        Measurements.Add(new { label="synthetic-raster-truth", requestedRow=173,requestedColumn=271,actualRow=truthRows.D,actualColumn=truthColumns.D,radius=65 });
        var original = Pixels(image);
        var output = Run(image, node);
        try
        {
            Found(output, 172.5, 270.5, 65, .25, "已知圆");
            using var pixel=((HImage)output.RuntimePorts!["preview"]!).GetGrayval(172d,335d);
            Check(pixel.ToDArr().SequenceEqual(new double[]{50,205,50}),"实际拟合圆显示绿色而非只显示参考框");
        }
        finally { Release(output); }
        Check(Pixels(image) == original, "拟合及预览不污染灰度输入像素");
        using var rgb = image.Compose3(image, image);
        output = Run(rgb, node);
        try
        {
            Found(output, 172.5, 270.5, 65, .25, "共享通道 RGB");
            using var channels = ((HImage)output.RuntimePorts!["preview"]!).CountChannels(); Check(channels.I == 3, "输出为彩色预览");
        }
        finally { Release(output); }
        Check(Pixels(image) == original, "RGB 预览不会通过共享通道修改原灰度图");
        foreach (var type in new[] { "uint2", "real" })
        {
            using var converted = image.ConvertImageType(type); output = Run(converted, node);
            try { Found(output, 172.5, 270.5, 65, .25, type); } finally { Release(output); }
        }
        output = Run(image, Set(node, "measureTransition", "negative")); try { Found(output, 172.5, 270.5, 65, .25, "圆内向外亮到暗"); } finally { Release(output); }
        output = Run(image, Set(node, "measureTransition", "positive")); try { NotFound(output, "错误极性"); } finally { Release(output); }
        using var dark = Disk(173, 271, 65, inverse: true);
        output = Run(dark, Set(node, "measureTransition", "positive")); try { Found(output, 172.5, 270.5, 65, .25, "圆内向外暗到亮"); } finally { Release(output); }
        using var blank = new HImage(); blank.GenImageConst("byte", 512, 384);
        output = Run(blank, node);
        try
        {
            NotFound(output, "无边缘空图"); using var pixel=((HImage)output.RuntimePorts!["preview"]!).GetGrayval(97d,195d);
            Check(pixel.ToDArr().SequenceEqual(new double[]{255,0,0}),"未检出时显示红色范围和明确状态");
        }
        finally { Release(output); }
        output = Run(image, Set(node, "measureThreshold", 65535)); try { NotFound(output, "高阈值"); } finally { Release(output); }
        output = Run(image, Set(node, "maximumRadius", 60)); try { NotFound(output, "结果半径筛选"); } finally { Release(output); }
        using var shifted = Disk(177, 268, 65);
        output = Run(shifted, node); try { Found(output, 176.5, 267.5, 65, .25, "参考中心附近偏移"); } finally { Release(output); }
        using var outsideCircle = new HRegion(); outsideCircle.GenCircle(50d, 50d, 25d);
        using var changed = image.PaintRegion(outsideCircle, 255d, "fill");
        output = Run(changed, node); try { Found(output, 172.5, 270.5, 65, .25, "框外干扰圆"); } finally { Release(output); }
        using var erased = new HRegion(0d,0d,172d,511d); using var halfCircle=image.PaintRegion(erased,30d,"fill");
        output=Run(halfCircle,Set(node,"minScore",.8));try{NotFound(output,"只有半圆且边缘利用率要求0.8");}finally{Release(output);}
        output=Run(image,Set(node,"maximumResidual",.01));try{NotFound(output,"严格残差拒绝不输出假圆");Check(output.Ports["residualRms"].GetDouble()>.01,"残差拒绝保留真实诊断数值");}finally{Release(output);}
        output=Run(image,Set(Set(node,"showSearchRegion",false),"showFittedCircle",false));
        try{Check(Pixels((HImage)output.RuntimePorts!["preview"]!)==Pixels(rgb),"关闭图形时预览保留输入像素，仍可读取几何结果");}finally{Release(output);}
        output=Run(image,Set(node,"showDiagnostics",true));
        try{Check(((CircleGeometryOverlay)output.RuntimePorts!["overlay"]!).MeasureRegions.Count>=48,"诊断层来自真实 HALCON 测量卡尺");}finally{Release(output);}
        using var missingRegion = new HRegion(100d, 196d, 248d, 346d); using var reduced = image.ReduceDomain(missingRegion);
        Reject(reduced, node, "普通缩小有效域也不能被 Metrology 忽略后读取无效像素");
        Reject(image, Node(), "缺少 ROI 明确提示绘制");
        foreach (var (key, value) in new (string, object)[] { ("roi", "0,0,5,5"), ("roi", "-1,195,249,347"), ("roi", "97,195,999,347"), ("numMeasures", 10.5), ("minScore", 0), ("minimumRadius", 100001), ("referenceRadius", 300), ("measureTransition", "bad"), ("searchHalfWidth", -1), ("measureSigma", 99), ("maximumResidual", "NaN") })
            Reject(image, Set(node, key, value), "无效参数拒绝：" + key + "=" + value);
        using var objects = image.ConcatObj(image);
        Reject(objects, node, "多图对象不静默只处理第一张");
        using var cancel = new CancellationTokenSource();
        var cancelled = false;
        try { Run(image, node, cancel.Token, _ => cancel.Cancel()); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && Pixels(image) == original, "原生测量前取消，输入保留且不发布输出");
        using var cancelLate = new CancellationTokenSource(); cancelled = false;
        try { Run(image, node, cancelLate.Token, text => { if (text.Contains("CIRCLE_FOUND")) cancelLate.Cancel(); }); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && Pixels(image) == original, "预览完成后取消也不发布半份结果");
        var timer = Stopwatch.StartNew();
        for (var round = 0; round < 100; round++)
        {
            output = Run(round % 2 == 0 ? image : blank, node);
            try { Check(output.Ports["count"].GetInt32() == (round % 2 == 0 ? 1 : 0), "连续替换无残留结果：" + round); }
            finally { Release(output); }
        }
        Measurements.Add(new { name = "100-circle-blank-runs", milliseconds = timer.Elapsed.TotalMilliseconds });
        var official = Path.Combine(Environment.GetEnvironmentVariable("HALCONIMAGES")!, "circle_plate.png");
        var officialHash = FileHash(official);
        using var plate = new HImage(official);
        var plateNode = Set(Set(Node(), "roi", "634,420,786,572"), "referenceRadius", 64d);
        output = Run(plate, plateNode); try { Found(output, 710, 496, 64, 2, "官方 circle_plate 图像（近似范围，无计量真值）"); } finally { Release(output); }
        Check(FileHash(official) == officialHash, "官方示例原文件哈希不变");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        var plateCopy = Path.Combine(_root, "images", "circle_plate.png"); File.Copy(official, plateCopy);
        image.WriteImage("png", 0, Path.Combine(_root, "images", "known-circle.png"));
        blank.WriteImage("png", 0, Path.Combine(_root, "images", "blank.png"));
        Projects(plateCopy, plateNode, node);
    }

    private static void Projects(string imageFile, NodeInstance detector, NodeInstance syntheticNode)
    {
        using var scope = new Scope();
        var reader = new NodeInstance { TypeId = HalconNodeProvider.ImageRead, DisplayName = "读取图像", X = 70, Y = 100, Parameters = new() { ["path"] = J(imageFile) } };
        var region = new InspectionRegion(Guid.NewGuid(), "圆测量范围", reader.Id, "image", new(634,420,786,572));
        var task = new InspectionTask(Guid.NewGuid(), "圆存在", region.Id, [detector.Id], [new(Guid.NewGuid(), detector.Id, "status", InspectionRuleKind.TextEquals, "/Code", Expected: CircleFindStatus.Found)]);
        var flow = new FlowDocument { Name = "圆查找检查", Nodes = [reader, detector], Edges = [new(Guid.NewGuid(), reader.Id, "image", detector.Id, "image")],
            InspectionRegions = [region], InspectionTasks = [task], DetectionPanels = [new(Guid.NewGuid(), reader.Id, Contents: [new(Guid.NewGuid(), reader.Id, Overlays: [new(detector.Id, Fields: ["diameter", "score"])])])] };
        var project = new ProjectStore().CreateProject(Path.Combine(_root, "圆查找检查.hvproj"), "圆查找检查");
        new ProjectStore().SaveProject(project, [flow]);
        var reopened = new ProjectStore().OpenProject(project.ProjectFilePath!);
        Check(reopened.Project.Flows.Single().Nodes.Single(n => n.Id == detector.Id).Parameters["roi"].GetString() == "634,420,786,572", "单文件项目重开恢复圆查找 ROI 和参数");
        Check(scope.Validator.Validate(flow).Count == 0, "注册后的类型端口与独立任务配置有效");
        using var run = scope.Engine.RunAsync(flow).GetAwaiter().GetResult();
        Check(run.Status == WorkflowRunStatus.Success && run.Inspection?.Verdict == InspectionVerdict.Ok, "官方图像整链路和必检判定 OK");
        var tab = new FlowTabViewModel(flow, scope.Registry, scope.Validator);
        var scene = DetectionOverlayScene.Create(tab, run, flow.DetectionPanels[0].Contents![0], 1280, 1024);
        Check(scene.Geometry.Any(g => g.Color == "LimeGreen" && g.Points.Count == 361), "原图上绘制实际拟合完整圆");
        Check(scene.Captions.Any(c => c.Text.Contains("直径")) && scene.Captions.All(c => !c.Text.Contains("检测状态未知")), "检测组合显示直径和明确的圆状态");
        Check(NodeOutputValue.From(run.NodeResults.Single(n => n.NodeId == detector.Id), Definition).Any(v => v.Name == "circle.Row"), "高级圆心坐标保留在节点输出");
        var invalid = flow with { Nodes = [reader, detector with { Parameters = new(detector.Parameters) { ["roi"] = J("0,0,2,2") } }], InspectionTasks = [], InspectionRegions = [] };
        using var invalidRun = scope.Engine.RunAsync(invalid).GetAwaiter().GetResult();
        Check(invalidRun.Status == WorkflowRunStatus.Failed, "无效配置沿引擎错误链传播");
        var blankReader = reader with { Parameters = new() { ["path"] = J(Path.Combine(_root, "images", "blank.png")) } };
        var syntheticRegion = region with { Bounds = new(97,195,249,347) };
        var synthetic = flow with { Nodes = [blankReader, syntheticNode with { Id = detector.Id }], InspectionRegions = [syntheticRegion] };
        using var missing = scope.Engine.RunAsync(synthetic).GetAwaiter().GetResult();
        Check(missing.Status == WorkflowRunStatus.Success && missing.Inspection?.Verdict == InspectionVerdict.Ng, "未找到圆不会作为产品 OK");
        Check(missing.SpatialOutputs![detector.Id]["preview"].Count == 1, "未找到圆的预览仍是一帧真实图像");
        var brokenTask = flow with { InspectionRegions = [region with { Bounds = new(0,0,2,2) }] };
        using var broken = scope.Engine.RunAsync(brokenTask).GetAwaiter().GetResult();
        Check(broken.Inspection?.Verdict == InspectionVerdict.Error, "测量失败的必检任务为 ERROR 而非 OK");
        var annotated = flow with { InspectionTasks = [], InspectionRegions = [], Nodes = [reader, detector, detector with { Id = Guid.NewGuid() }] };
        annotated = annotated with { Edges = [..annotated.Edges, new(Guid.NewGuid(),detector.Id,"preview",annotated.Nodes[2].Id,"image")] };
        Check(scope.Validator.Validate(annotated).Any(i => i.Message.Contains("标注")), "阻止检测预览误接圆查找原图端口");
        var copy = new ProjectStore().ExportFlow(flow, Path.Combine(_root, "circle.flow"));
        Check(File.Exists(copy), "圆查找流程可导出");
    }

    private static void Ui()
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(Path.Combine(_root, "圆查找检查.hvproj"));
        window.Show(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var vm = (WorkflowWorkspaceViewModel)window.DataContext;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            Check(vm.NodeCatalog.Count(n => n.TypeId == HalconCircleNodeProvider.CircleFind) == 1, "产品节点库注册圆查找且不重复");
            var node = vm.SelectedFlow!.Nodes.Single(n => n.TypeId == HalconCircleNodeProvider.CircleFind);
            vm.SelectedFlow.SelectNode(node.Id, false);
            var root = (FrameworkElement)window.Content;
            foreach (var width in new[] { 1180d, 1366d, 1580d })
            {
                root.Measure(new Size(width, 680)); root.Arrange(new Rect(0,0,width,680)); root.UpdateLayout();
                Check(node.PrimaryParameters.Count() == 6 && node.AdvancedParameters.Count() == 9, "圆参数基础/高级分组：" + width);
                var advanced = Descendants(root).OfType<Expander>().Single(e => System.Windows.Automation.AutomationProperties.GetName(e) == "高级测量参数");
                Check(!advanced.IsExpanded, "高级圆参数默认折叠：" + width);
                advanced.IsExpanded = true; root.UpdateLayout();
                var right = (FrameworkElement)window.FindName("RightPanel");
                var scroll = Descendants(right).OfType<ScrollViewer>().First(); scroll.ScrollToEnd(); root.UpdateLayout();
                Check(scroll.ScrollableHeight > 0, "高级圆参数可滚动到达：" + width);
                foreach(var button in Descendants(advanced).OfType<Button>().Where(b=>b.IsVisible&&b.ActualWidth>0))
                {var point=button.TranslatePoint(new Point(),right);Check(point.X>=-.5&&point.X+button.ActualWidth<=right.ActualWidth+.5,"高级操作无横向遮挡："+width);}
                advanced.IsExpanded = false; scroll.ScrollToHome(); root.UpdateLayout();
            }
            var roiParameter = node.Parameters.Single(p => p.Definition.Id == "roi");
            var draw = Descendants(root).OfType<Button>().Single(b => b.Tag == roiParameter && b.Content as string == "绘制矩形范围");
            draw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var wait=Stopwatch.StartNew();
            while(!(bool)typeof(MainWindow).GetField("_isDrawingRoi",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!&&wait.ElapsedMilliseconds<5000)
            {window.Dispatcher.Invoke(()=>{},DispatcherPriority.Background);Thread.Sleep(5);}
            Check((bool)typeof(MainWindow).GetField("_isDrawingRoi", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!, "真实绘制入口可为圆节点启动 ROI 编辑");
            Check(typeof(MainWindow).GetField("_inspectionRegionDraw",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window) is Action<Rectangle2D>, "绑定任务的节点绘制入口必须修改实际生效的任务区域，而非被覆盖的局部参数");
            var actualRegion=new Rectangle2D(630,416,790,576);
            var commit=(Action<Rectangle2D>)typeof(MainWindow).GetField("_inspectionRegionDraw",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            commit(actualRegion);
            Check(vm.SelectedFlow.Document.InspectionRegions.Single().Bounds==actualRegion&&vm.SelectedFlow.Document.Nodes.Single(n=>n.Id==node.Id).Parameters["roi"].GetString()=="634,420,786,572","命名任务区域已更新，不修改被覆盖的节点局部参数");
            using(var scope=new Scope())using(var actualRun=scope.Engine.RunAsync(vm.SelectedFlow.Document).GetAwaiter().GetResult())
                Check(((CircleGeometryOverlay)actualRun.RuntimeOutputs![node.Id]["overlay"]!).SearchRegion==actualRegion&&actualRun.Inspection?.Verdict==InspectionVerdict.Ok,"重新执行实际使用重绘后的任务区域");
            Check(!roiParameter.CanClearRoi&&roiParameter.Name.Contains("任务区域"),"绑定状态清晰标识，禁止局部清空无效操作");
            Check(ToolTipService.GetInitialShowDelay(draw) == 1500 && ToolTipService.GetBetweenShowDelay(draw) == 0, "圆 ROI 入口使用1.5秒提示和切换等待");
            typeof(MainWindow).GetMethod("FinishRoiDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            typeof(MainWindow).GetField("_inspectionOutlineRegion",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,vm.SelectedFlow.Document.InspectionRegions.Single());
            var canvas=(Canvas)window.FindName("RoiDrawingOverlay");canvas.Visibility=Visibility.Visible;root.UpdateLayout();
            typeof(MainWindow).GetMethod("RenderConfiguredRois",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            var outline=(System.Collections.IEnumerable)typeof(MainWindow).GetField("_roiOutlines",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var elements=outline.Cast<FrameworkElement>().ToArray();var rectangle=elements.OfType<System.Windows.Shapes.Rectangle>().Single();
            var area=new Rect(Canvas.GetLeft(rectangle),Canvas.GetTop(rectangle),rectangle.Width,rectangle.Height);
            Check(elements.OfType<TextBlock>().Where(t=>t.Visibility==Visibility.Visible).All(t=>!new Rect(Canvas.GetLeft(t),Canvas.GetTop(t),t.DesiredSize.Width,t.DesiredSize.Height).IntersectsWith(area)),"任务 ROI 标签位于框外，不遮住待测圆");
            Check(vm.SelectedFlow.Document.Nodes.Single(n=>n.Id==node.Id).Parameters["roi"].GetString() == "634,420,786,572", "退出绘制不改无效的局部参数");
            vm.SelectedFlow.SetParameter(node.Id, "measureThreshold", J(30d)); vm.SaveAll();
            vm.OpenProject(Path.Combine(_root, "圆查找检查.hvproj"));
            Check(vm.SelectedFlow!.Document.Nodes.Single(n=>n.Id==node.Id).Parameters["measureThreshold"].GetDouble() == 30d, "属性调整保存重开生效");
            var editor=new InspectionTaskEditorWindow(window,vm.SelectedFlow,vm.NodeCatalog,null);editor.Show();editor.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            try
            {
                var before=editor.Tasks.Single().Rules.Count;
                typeof(InspectionTaskEditorWindow).GetMethod("AddRule_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,[editor,new RoutedEventArgs()]);
                var rule=editor.Tasks.Single().Rules.Last();
                Check(editor.Tasks.Single().Rules.Count==before+1&&rule.PortId=="status"&&rule.Pointer=="/Code"&&rule.Expected==CircleFindStatus.Found&&rule.Kind==InspectionRuleKind.TextEquals,"任务编辑器新增圆规则使用正确状态，不误用快捷匹配判定");
            }
            finally{editor.Close();}
        }
        finally { window.Close(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); app.Shutdown(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root,i); yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    private static string Pixels(HImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var channels = image.CountChannels();
        for (var i=1;i<=channels.I;i++) { using var plane=image.AccessChannel(i); var pointer=plane.GetImagePointer1(out string type,out int w,out int h); var bytes=new byte[w*h*(type=="byte"?1:type=="uint2"?2:4)]; Marshal.Copy(pointer,bytes,0,bytes.Length); hash.AppendData(bytes); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string FileHash(string path) { using var file=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Save() => File.WriteAllText(Path.Combine(_root,"summary.json"),JsonSerializer.Serialize(new { count=Checks.Count, checks=Checks, measurements=Measurements },new JsonSerializerOptions{WriteIndented=true}));
    private sealed class Scope : IDisposable
    {
        private readonly HalconCameraManager _camera = new(); private readonly DlModelSessionCache _dl = new(); private readonly ShapeMatchModelCache _models = new();
        public NodeRegistry Registry { get; } = BuiltInNodeCatalog.Create();
        public WorkflowGraphValidator Validator { get; }
        public WorkflowExecutionEngine Engine { get; }
        public Scope()
        {
            Registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(),new HalconCameraCaptureExecutor(_camera),new HalconThresholdExecutor(),new HalconBlobAnalysisExecutor(),new HalconStraightLineFindExecutor(),new HalconSemanticSegmentationExecutor(_dl),new HalconClassificationExecutor(_dl),new HalconObjectDetectionExecutor(_dl),new HalconQuickMatchExecutor(_models),new HalconPositionCorrectionExecutor()]);
            Registry.Register(Provider,[new HalconCircleFindExecutor()]); Validator = new(Registry); Engine = new(Registry,Validator);
        }
        public void Dispose() { _camera.Dispose(); _dl.Dispose(); _models.Dispose(); }
    }
}
