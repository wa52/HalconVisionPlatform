using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private static readonly List<object> Observations = [];
    private static string Root = "";
    private static NodeDefinition Definition => new HalconLineCircleNodeProvider().GetDefinitions().Single();
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Checks.Add(message); }
    private static NodeInstance Measure() => new() { TypeId = HalconLineCircleNodeProvider.LineCircle, DisplayName = "线圆测量", X = 710, Y = 150,
        Parameters = Definition.Parameters.ToDictionary(p => p.Id, p => p.DefaultValue!.Value) };
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0]) || File.Exists(args[0])) return 2;
        Root = Path.GetFullPath(args[0]); Directory.CreateDirectory(Root);
        try { Core(); Pipeline(); Ui(); Save(); Console.WriteLine($"PASS {Checks.Count} 项线圆测量检查"); return 0; }
        catch (Exception error) { Save(); File.WriteAllText(Path.Combine(Root, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static void Save() => File.WriteAllText(Path.Combine(Root, "summary.json"), JsonSerializer.Serialize(new { count = Checks.Count, Checks, Observations }, new JsonSerializerOptions { WriteIndented = true }));
    private static NodeExecutionContext Context(HImage image, Line2D? line, Circle2D? circle)
    {
        image.GetImageSize(out int w, out int h); var id = Guid.NewGuid();
        var frame = new FrameReference(id, Guid.NewGuid(), Guid.NewGuid(), "image", Guid.NewGuid(), w, h, 1, false, false, "fixture");
        return new(Measure(), new Dictionary<string, JsonElement>(), default, _ => { },
            new Dictionary<string, object?> { ["image"] = image, ["line"] = line, ["circle"] = circle }, id,
            new Dictionary<string, FrameReference> { ["image"] = frame, ["line"] = frame, ["circle"] = frame });
    }
    private static NodeExecutionOutput Execute(NodeExecutionContext context) => new HalconLineCircleExecutor().ExecuteAsync(context).GetAwaiter().GetResult();
    private static void Release(NodeExecutionOutput output)
    { var images = output.RuntimePorts!.Values.OfType<HImage>().ToArray(); foreach (var i in images) i.Dispose(); Check(images.All(i => !i.IsInitialized()), "输出预览释放"); }
    private static void Reject(NodeExecutionContext context, string message)
    { var rejected = false; try { Release(Execute(context)); } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    private static string Pixels(HImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var channels = image.CountChannels();
        for (var i = 1; i <= channels.I; i++) { using var plane = image.AccessChannel(i); var p = plane.GetImagePointer1(out string type, out int w, out int h); var bytes = new byte[w * h]; Marshal.Copy(p, bytes, 0, bytes.Length); hash.AppendData(bytes); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Core()
    {
        using var image = new HImage(); image.GenImageConst("byte", 512, 512); var before = Pixels(image);
        var cases = new[] {
            (new Line2D(100,50,100,450), new Circle2D(200,200,30), 100d,70d,70d,"相离",true),
            (new Line2D(50,100,450,100), new Circle2D(200,200,30), 100d,70d,70d,"相离",true),
            (new Line2D(100,50,100,450), new Circle2D(130,200,30), 30d,0d,0d,"相切",true),
            (new Line2D(100,50,100,450), new Circle2D(110,200,30), 10d,0d,-20d,"相交",true),
            (new Line2D(100,50,100,450), new Circle2D(100,200,30), 0d,0d,-30d,"相交",true),
            (new Line2D(50,50,250,250), new Circle2D(200,100,20), Math.Sqrt(5000),Math.Sqrt(5000)-20,Math.Sqrt(5000)-20,"相离",true),
            (new Line2D(100,50,100,100), new Circle2D(200,300,30), 100d,70d,70d,"相离",false) };
        foreach (var (line, circle, distance, gap, clearance, relation, within) in cases)
        {
            var output = Execute(Context(image, line, circle));
            try
            {
                var m = (LineCircleMeasurement)output.RuntimePorts!["measurement"]!;
                Check(Math.Abs(m.CenterToLine-distance)<1e-8 && Math.Abs(m.BoundaryToLine-gap)<1e-8 && Math.Abs(m.SignedClearance-clearance)<1e-8 && m.Relation==relation, "解析距离及HALCON垂距一致："+relation);
                Check(Math.Abs(m.CenterNormal.Length-distance)<1e-8 && m.ProjectionWithinSegment==within, "垂线长度及延长线语义正确");
                Check(m.BoundaryLine is null ? gap < 1e-8 : Math.Abs(m.BoundaryLine.Length-gap)<1e-8, "边界连线与最短距离一致");
                if (m.BoundaryLine is {} border) Check(Math.Abs(HMisc.DistancePp(border.RowBegin,border.ColumnBegin,circle.Row,circle.Column)-circle.Radius)<1e-8 && HMisc.DistancePl(border.RowEnd,border.ColumnEnd,line.RowBegin,line.ColumnBegin,line.RowEnd,line.ColumnEnd)<1e-8, "最短线端点位于圆周及直线上");
                Check(output.Ports["status"].Deserialize<LineCircleStatus>()!.Code==LineCircleStatus.Measured, "测量状态不是产品合格结论");
                Observations.Add(m);
            } finally { Release(output); }
            var reverse=Execute(Context(image,new(line.RowEnd,line.ColumnEnd,line.RowBegin,line.ColumnBegin),circle));
            try { Check(Math.Abs(reverse.Ports["signedClearance"].GetDouble()-clearance)<1e-8, "反转直线端点不改变净距符号"); } finally { Release(reverse); }
        }
        var random=new Random(42);
        for(var i=0;i<30;i++)
        {
            var angle=random.NextDouble()*Math.PI; var circle=new Circle2D(256,256,20); var distance=30+random.NextDouble()*60;
            var row=256+distance*Math.Cos(angle);var col=256+distance*Math.Sin(angle);
            var line=new Line2D(row-100*Math.Sin(angle),col+100*Math.Cos(angle),row+100*Math.Sin(angle),col-100*Math.Cos(angle));
            var output=Execute(Context(image,line,circle));try{Check(Math.Abs(output.Ports["centerToLine"].GetDouble()-distance)<1e-8,"固定随机种子方向距离 "+i);}finally{Release(output);}
        }
        for(var i=0;i<100;i++)
        {
            var result=Execute(Context(image,i%2==0?cases[0].Item1:null,cases[0].Item2));
            try{Check(result.Ports["measurement"].ValueKind==(i%2==0?JsonValueKind.Object:JsonValueKind.Null),"交替重跑不残留旧测量 "+i);}finally{Release(result);}
        }
        var flags=Context(image,cases[0].Item1,cases[0].Item2);
        var hidden=Execute(flags with{Node=flags.Node with{Parameters=new(){{"showNormal",J(false)},{"showBoundaryLine",J(false)}}}});
        try{var overlay=(LineCircleOverlay)hidden.RuntimePorts!["overlay"]!;Check(!overlay.ShowNormal&&!overlay.ShowBoundaryLine&&hidden.Ports["centerToLine"].GetDouble()==100,"绘图开关不改变测量数据");}finally{Release(hidden);}
        Check(Pixels(image)==before,"全部测量及绘制不改变原始灰度图");
        using(var rgb=image.Compose3(image,image)){var hash=Pixels(rgb);var output=Execute(Context(rgb,cases[0].Item1,cases[0].Item2));try{using var px=((HImage)output.RuntimePorts!["preview"]!).GetGrayval(140d,200d);Check(px.ToDArr().SequenceEqual(new double[]{255,210,0}),"共享RGB输入的预览显示真实金色间隙线");}finally{Release(output);}Check(Pixels(rgb)==hash,"共享RGB图像未污染");}
        var c=Context(image,cases[0].Item1,cases[0].Item2);
        foreach(var port in new[]{"line","circle"})
        {
            foreach(var change in new Func<FrameReference,FrameReference>[] {f=>f with{RunId=Guid.NewGuid()},f=>f with{FrameId=Guid.NewGuid()},f=>f with{SpaceId=Guid.NewGuid()},f=>f with{SourceNodeId=Guid.NewGuid()},f=>f with{SourcePortId="other"},f=>f with{Width=42},f=>f with{Height=42},f=>f with{Count=2},f=>f with{ImageIndex=2},f=>f with{Annotated=true},f=>f with{Corrected=true}})
                Reject(c with{InputFrames=new Dictionary<string,FrameReference>(c.InputFrames!){[port]=change(c.InputFrames![port])}},"拒绝跨帧/空间/来源/尺寸或标注几何："+port);
            var missing=Execute(c with{Inputs=new Dictionary<string,JsonElement>{{port,J(port=="line"?(object)cases[0].Item1:cases[0].Item2)}},RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){[port]=null}});
            try{Check(missing.Ports["centerToLine"].ValueKind==JsonValueKind.Null&&missing.Ports["signedClearance"].ValueKind==JsonValueKind.Null&&((LineCircleOverlay)missing.RuntimePorts!["overlay"]!).Measurement is null,"缺失几何不回退旧JSON，不输出零值或旧图形："+port);}finally{Release(missing);}
            Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){[port]="wrong"}},"错误类型不视作未检出："+port);
        }
        Reject(c with{InputFrames=null},"无帧来源拒绝执行");
        foreach(var bad in new[]{new Line2D(100,100,100,100),new Line2D(double.NaN,100,200,200),new Line2D(-1,100,200,200)})Reject(Context(image,bad,cases[0].Item2),"拒绝退化或非法直线");
        foreach(var bad in new[]{new Circle2D(200,200,0),new Circle2D(double.PositiveInfinity,200,20),new Circle2D(2,2,20)})Reject(Context(image,cases[0].Item1,bad),"拒绝非法或越界圆");
        Reject(Context(image,new(0,400,100,500),new(450,450,20)),"垂足在图像外拒绝测量");
        Reject(c with{Node=c.Node with{Parameters=new(){{"taskRoi",J("0,0,120,120")}}}},"任务范围未覆盖全部几何时拒绝");
        using(var domain=new HRegion(0d,0d,150d,500d))using(var reduced=image.ReduceDomain(domain))Reject(Context(reduced,cases[0].Item1,cases[0].Item2),"测量包围范围缺无效像素时拒绝");
        using(var multi=image.ConcatObj(image))Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){["image"]=multi}},"多幅输入拒绝");
        Reject(c with{Node=c.Node with{Parameters=new(){{"showNormal",J("wrong")}}}},"非法显示开关拒绝");
        foreach(var atLog in new[]{false,true})using(var cancel=new CancellationTokenSource())
        {if(!atLog)cancel.Cancel();var caught=false;try{Release(Execute(c with{CancellationToken=cancel.Token,Log=_=>{if(atLog)cancel.Cancel();}}));}catch(OperationCanceledException){caught=true;}Check(caught,"启动前/计算后取消无输出");}
    }
    private static FlowDocument Fixture()
    {
        using(var black=new HImage())
        {black.GenImageConst("byte",512,384);using var band=new HRegion(90d,30d,110d,480d);using var circle=new HRegion();circle.GenCircle(240d,300d,40d);using var a=black.PaintRegion(band,220d,"fill");using var b=a.PaintRegion(circle,220d,"fill");using var blurred=b.GaussFilter(3);blurred.WriteImage("png",0,Path.Combine(Root,"line-circle.png"));black.WriteImage("png",0,Path.Combine(Root,"blank.png"));}
        var source=new NodeInstance{TypeId=HalconNodeProvider.ImageRead,DisplayName="读取图像",X=20,Y=145,Parameters=new(){{"path",J(Path.Combine(Root,"line-circle.png"))}}};
        var line=new NodeInstance{TypeId=HalconNodeProvider.StraightLineFind,DisplayName="直线查找",X=300,Y=25,Parameters=new(){{"roi",J("60,40,105,470")},{"lineOrientation",J("horizontal")},{"measureTransition",J("all")}}};
        var circleDef=new HalconCircleNodeProvider().GetDefinitions().Single();var circleNode=new NodeInstance{TypeId=HalconCircleNodeProvider.CircleFind,DisplayName="圆查找",X=300,Y=315,Parameters=circleDef.Parameters.ToDictionary(p=>p.Id,p=>p.DefaultValue!.Value)};
        circleNode.Parameters["roi"]=J("180,240,300,360");circleNode.Parameters["referenceRadius"]=J(40d);var measure=Measure();
        var lr=new InspectionRegion(Guid.NewGuid(),"直线范围",source.Id,"image",new(60,40,105,470));var cr=new InspectionRegion(Guid.NewGuid(),"圆范围",source.Id,"image",new(180,240,300,360));var mr=new InspectionRegion(Guid.NewGuid(),"线圆测量范围",source.Id,"image",new(50,20,310,490));
        var tasks=new List<InspectionTask>{new(Guid.NewGuid(),"直线存在",lr.Id,[line.Id],[new(Guid.NewGuid(),line.Id,"status",InspectionRuleKind.TextEquals,"/Code",Expected:LineFindStatus.Found)]),new(Guid.NewGuid(),"圆存在",cr.Id,[circleNode.Id],[new(Guid.NewGuid(),circleNode.Id,"status",InspectionRuleKind.TextEquals,"/Code",Expected:CircleFindStatus.Found)]),new(Guid.NewGuid(),"线圆间隙公差",mr.Id,[measure.Id],[new(Guid.NewGuid(),measure.Id,"centerToLine",InspectionRuleKind.NumberRange,Minimum:149,Maximum:152),new(Guid.NewGuid(),measure.Id,"boundaryToLine",InspectionRuleKind.NumberRange,Minimum:109,Maximum:112)])};
        return new(){SchemaVersion=2,Name="线圆尺寸检查",Nodes=[source,line,circleNode,measure],Edges=[new(Guid.NewGuid(),source.Id,"image",line.Id,"image"),new(Guid.NewGuid(),source.Id,"image",circleNode.Id,"image"),new(Guid.NewGuid(),source.Id,"image",measure.Id,"image"),new(Guid.NewGuid(),line.Id,"line",measure.Id,"line"),new(Guid.NewGuid(),circleNode.Id,"circle",measure.Id,"circle")],InspectionRegions=[lr,cr,mr],InspectionTasks=tasks,DetectionPanels=[new(Guid.NewGuid(),source.Id,Contents:[new(Guid.NewGuid(),source.Id,Overlays:[new(measure.Id,Fields:["centerToLine","boundaryToLine"])])])]};
    }
    private static void Pipeline()
    {
        var flow=Fixture();var measure=flow.Nodes.Last();using var camera=new HalconCameraManager();var registry=BuiltInNodeCatalog.Create();
        registry.Register(new HalconNodeProvider(),[new HalconImageReadExecutor(),new HalconCameraCaptureExecutor(camera),new HalconThresholdExecutor(),new HalconBlobAnalysisExecutor(),new HalconStraightLineFindExecutor(),new HalconSemanticSegmentationExecutor(),new HalconClassificationExecutor(),new HalconObjectDetectionExecutor(),new HalconQuickMatchExecutor(),new HalconPositionCorrectionExecutor()]);
        registry.Register(new HalconCircleNodeProvider(),[new HalconCircleFindExecutor()]);registry.Register(new HalconLineCircleNodeProvider(),[new HalconLineCircleExecutor()]);var validator=new WorkflowGraphValidator(registry);var engine=new WorkflowExecutionEngine(registry,validator);
        Check(validator.Validate(flow).Count==0,"真实四节点及任务图校验");
        using(var run=engine.RunAsync(flow).GetAwaiter().GetResult())
        {
            Check(run.Status==WorkflowRunStatus.Success&&run.Inspection?.Verdict==InspectionVerdict.Ok,"实际读图→找线/找圆→测量→三任务OK");
            var result=run.NodeResults.Single(n=>n.NodeId==measure.Id);var m=result.Outputs["measurement"].Deserialize<LineCircleMeasurement>()!;
            File.WriteAllText(Path.Combine(Root,"actual-result.json"),JsonSerializer.Serialize(run.NodeResults,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"实际拟合：垂距 {m.CenterToLine:R}，圆周距 {m.BoundaryToLine:R}，圆半径 {m.Circle.Radius:R}，直线 {m.Line}");
            Check(Math.Abs(m.CenterToLine-150)<.25&&Math.Abs(m.BoundaryToLine-110)<.25,"实际Metrology测量接近名义150/110像素栅格尺寸");Observations.Add(new{pipeline=m});
            Check(run.SpatialOutputs![measure.Id]["measurement"].FrameId==run.SpatialOutputs[flow.Nodes[0].Id]["image"].FrameId,"测量结果保持原图帧身份");
            var scene=DetectionOverlayScene.Create(new(flow,registry,validator),run,flow.DetectionPanels[0].Contents![0],512,384);
            Check(scene.Geometry.Any(g=>g.Color=="Gold")&&scene.Geometry.Any(g=>g.Color=="DeepSkyBlue")&&scene.Captions.Any(c=>c.Text.Contains("圆心到直线")),"同图叠加显示垂线、间隙线及数值");
            File.WriteAllText(Path.Combine(Root,"actual-result.json"),JsonSerializer.Serialize(run.NodeResults,new JsonSerializerOptions{WriteIndented=true}));
            var displayFlow=new FlowTabViewModel(flow,registry,validator);
            using(var view=new DetectionContentView(new(Guid.NewGuid(),measure.Id,View:"检测结果"),()=>displayFlow,()=>run))
            {
                var grid=(DataGrid)typeof(DetectionContentView).GetField("_values",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
                var fields=grid.Items.Cast<NodeOutputValue>().Select(v=>v.Name).ToArray();
                Check(new[]{"centerToLine","boundaryToLine","signedClearance"}.All(fields.Contains),"检测结果表包含线圆三项数值");
            }
            var mt=flow.InspectionTasks.Last();var exact=mt with{Rules=[mt.Rules[0] with{Minimum=m.CenterToLine,Maximum=m.CenterToLine}]};Check(ProductInspectionEvaluator.Evaluate(flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),exact]},run.Id,run.Status,run.NodeResults,run.Errors).Verdict==InspectionVerdict.Ok,"公差边界用原精度闭区间");
        }
        var task=flow.InspectionTasks.Last();var ng=flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),task with{Rules=[task.Rules[0] with{Maximum=10,Minimum=0}]}]};
        using(var run=engine.RunAsync(ng).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Ng,"故意超差为NG而非执行成功即OK");
        var source=flow.Nodes[0];var blank=flow with{Nodes=flow.Nodes.Select(n=>n.Id==source.Id?n with{Parameters=new(){{"path",J(Path.Combine(Root,"blank.png"))}}}:n).ToList()};
        using(var run=engine.RunAsync(blank).GetAwaiter().GetResult()){var result=run.NodeResults.Single(n=>n.NodeId==measure.Id);Check(run.Inspection?.Verdict==InspectionVerdict.Error&&result.Outputs["measurement"].ValueKind==JsonValueKind.Null,"空图失测为ERROR，无假零或旧几何");}
        var second=source with{Id=Guid.NewGuid(),DisplayName="独立读取"};var mixed=flow with{InspectionTasks=[],InspectionRegions=[],Nodes=[..flow.Nodes,second],Edges=flow.Edges.Select(e=>e.ToNodeId==flow.Nodes[2].Id?e with{FromNodeId=second.Id}:e).ToList()};
        using(var run=engine.RunAsync(mixed).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Failed&&run.NodeResults.Single(n=>n.NodeId==measure.Id).Error!.Contains("FRAME_MISMATCH"),"同一图片独立读取的跨帧几何被阻断");
        var wrong=flow with{Edges=flow.Edges.Select(e=>e.ToNodeId==measure.Id&&e.ToPortId=="image"?e with{FromNodeId=flow.Nodes[1].Id,FromPortId="preview"}:e).ToList()};Check(validator.Validate(wrong).Any(i=>i.Message.Contains("标注")),"标注预览接入原图在设计时拒绝");
        var store=new ProjectStore();var project=store.CreateProject(Path.Combine(Root,"线圆尺寸检查.hvproj"),"线圆尺寸检查");store.SaveProject(project,[flow]);var loaded=store.OpenProject(project.ProjectFilePath!);Check(loaded.Project.Flows.Single().Nodes.Count==4&&loaded.Project.Flows.Single().InspectionTasks.Count==3,"方案保存恢复四节点及三任务");store.SaveProject(project with{ProjectFilePath=Path.Combine(Root,"线圆尺寸NG检查.hvproj")},[ng]);
        for(var i=0;i<15;i++){using var run=engine.RunAsync(flow).GetAwaiter().GetResult();Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"重复完整运行 "+i);var images=run.RuntimeOutputs!.Values.SelectMany(v=>v.Values).OfType<HImage>().ToArray();run.Dispose();Check(images.All(x=>!x.IsInitialized()),"每轮运行结果释放 "+i);}
    }
    private static void Ui()
    {
        var app=new App();app.InitializeComponent();var window=new MainWindow(Path.Combine(Root,"线圆尺寸检查.hvproj"));
        try
        {
            window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);var vm=(WorkflowWorkspaceViewModel)window.DataContext;
            Check(vm.NodeCatalog.Any(n=>n.TypeId==HalconLineCircleNodeProvider.LineCircle),"真实WPF节点库注册线圆测量");var flow=vm.SelectedFlow!;var selected=flow.Nodes.Single(n=>n.TypeId==HalconLineCircleNodeProvider.LineCircle);flow.SelectNode(selected.Id,false);window.UpdateLayout();
            foreach(var width in new[]{1180d,1366d,1580d}){var root=(FrameworkElement)window.Content;root.Measure(new Size(width,680));root.Arrange(new Rect(0,0,width,680));root.UpdateLayout();Check(selected.PrimaryParameters.Count()==2,"仅两个绘图开关，公差配置复用任务");}
            var editor=new InspectionTaskEditorWindow(window,flow,vm.NodeCatalog,null);try{editor.Show();editor.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);((ListBox)editor.FindName("TaskList")).SelectedIndex=2;typeof(InspectionTaskEditorWindow).GetMethod("AddRule_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(editor,[editor,new RoutedEventArgs()]);var rule=editor.Tasks.Last().Rules.Last();Check(rule.PortId=="boundaryToLine"&&rule.Kind==InspectionRuleKind.NumberRange&&rule.Pointer=="","任务编辑器默认创建线圆数值公差");}finally{editor.Close();}
            vm.SaveAll();Check(new ProjectStore().OpenProject(Path.Combine(Root,"线圆尺寸检查.hvproj")).Project.Flows.Single().Edges.Count==5,"WPF保存恢复全部接线");
        }finally{window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
}
