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
    private static NodeDefinition Definition => new HalconLinePairNodeProvider().GetDefinitions().Single();
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Checks.Add(message); }
    private static NodeInstance Measure() => new() { TypeId = HalconLinePairNodeProvider.LinePair, DisplayName = "线线测量", X = 710, Y = 150,
        Parameters = Definition.Parameters.ToDictionary(p => p.Id, p => p.DefaultValue!.Value) };
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0]) || File.Exists(args[0])) return 2;
        Root = Path.GetFullPath(args[0]); Directory.CreateDirectory(Root);
        try { Core(); Pipeline(); Ui(); Save(); Console.WriteLine($"PASS {Checks.Count} 项线线测量检查"); return 0; }
        catch (Exception error) { Save(); File.WriteAllText(Path.Combine(Root, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static void Save() => File.WriteAllText(Path.Combine(Root, "summary.json"), JsonSerializer.Serialize(new { count = Checks.Count, Checks, Observations }, new JsonSerializerOptions { WriteIndented = true }));
    private static NodeExecutionContext Context(HImage image,Line2D? a,Line2D? b)
    {
        image.GetImageSize(out int w,out int h);var id=Guid.NewGuid();
        var frame=new FrameReference(id,Guid.NewGuid(),Guid.NewGuid(),"image",Guid.NewGuid(),w,h,1,false,false,"fixture");
        return new(Measure(),new Dictionary<string,JsonElement>(),default,_=>{},
            new Dictionary<string,object?>{{"image",image},{"lineA",a},{"lineB",b}},id,
            new Dictionary<string,FrameReference>{{"image",frame},{"lineA",frame},{"lineB",frame}});
    }
    private static NodeExecutionOutput Execute(NodeExecutionContext context) => new HalconLinePairExecutor().ExecuteAsync(context).GetAwaiter().GetResult();
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
        using var image=new HImage();image.GenImageConst("byte",512,512);var before=Pixels(image);
        var cases=new[]{
            (new Line2D(100,50,100,450),new Line2D(200,50,200,450),100d,0d),
            (new Line2D(50,100,450,100),new Line2D(50,200,450,200),100d,0d),
            (new Line2D(100,100,100,150),new Line2D(100,250,100,300),100d,0d),
            (new Line2D(100,100,100,300),new Line2D(100,200,100,400),0d,0d),
            (new Line2D(200,100,200,300),new Line2D(100,200,300,200),0d,90d),
            (new Line2D(200,50,200,450),new Line2D(100,100,300,300),0d,45d),
            (new Line2D(100,100,100,200),new Line2D(200,300,300,300),Math.Sqrt(20000),90d)};
        foreach(var(a,b,distance,angle)in cases)
        {
            foreach(var (first,second) in new[]{(a,b),(b,a),(new Line2D(a.RowEnd,a.ColumnEnd,a.RowBegin,a.ColumnBegin),b),(a,new Line2D(b.RowEnd,b.ColumnEnd,b.RowBegin,b.ColumnBegin))})
            {
                var result=Execute(Context(image,first,second));
                try
                {
                    var m=(LinePairMeasurement)result.RuntimePorts!["measurement"]!;
                    Check(Math.Abs(m.SegmentDistance-distance)<1e-7&&Math.Abs(m.AngleDegrees-angle)<1e-7,"原生距离/小夹角、交换与反向不变");
                    Check(Math.Abs(HMisc.DistancePp(m.ClosestA.Row,m.ClosestA.Column,m.ClosestB.Row,m.ClosestB.Column)-distance)<1e-7,"最近点连线长度正确");
                    Check(On(m.ClosestA,first)&&On(m.ClosestB,second),"测量端点在有限线段上，非延长线");
                    Check(result.Ports["status"].Deserialize<LinePairStatus>()!.Code==LinePairStatus.Measured,"测量状态不等同产品合格");Observations.Add(m);
                }finally{Release(result);}
            }
        }
        var random=new Random(42);
        for(var i=0;i<30;i++)
        {
            var theta=random.NextDouble()*Math.PI;var d=20+random.NextDouble()*60;var dr=Math.Cos(theta);var dc=Math.Sin(theta);
            var a=new Line2D(256-100*dr,256-100*dc,256+100*dr,256+100*dc);
            var b=new Line2D(a.RowBegin+d*dc,a.ColumnBegin-d*dr,a.RowEnd+d*dc,a.ColumnEnd-d*dr);
            var result=Execute(Context(image,a,b));try{Check(Math.Abs(result.Ports["segmentDistance"].GetDouble()-d)<1e-7&&Math.Abs(result.Ports["angle"].GetDouble())<1e-7,"随机方向平行线 "+i);}finally{Release(result);}
        }
        var c=Context(image,cases[0].Item1,cases[0].Item2);
        foreach(var port in new[]{"lineA","lineB"})
        {
            foreach(var change in new Func<FrameReference,FrameReference>[] {f=>f with{RunId=Guid.NewGuid()},f=>f with{FrameId=Guid.NewGuid()},f=>f with{SpaceId=Guid.NewGuid()},f=>f with{SourceNodeId=Guid.NewGuid()},f=>f with{SourcePortId="other"},f=>f with{Width=42},f=>f with{Height=42},f=>f with{Count=2},f=>f with{ImageIndex=2},f=>f with{Annotated=true},f=>f with{Corrected=true}})
                Reject(c with{InputFrames=new Dictionary<string,FrameReference>(c.InputFrames!){[port]=change(c.InputFrames![port])}},"拒绝跨帧/空间/来源："+port);
            var missing=Execute(c with{Inputs=new Dictionary<string,JsonElement>{{port,J(cases[0].Item1)}},RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){[port]=null}});
            try{Check(missing.Ports["measurement"].ValueKind==JsonValueKind.Null&&missing.Ports["angle"].ValueKind==JsonValueKind.Null&&missing.Ports["segmentDistance"].ValueKind==JsonValueKind.Null,"失测不使用旧JSON或假零");}finally{Release(missing);}
            Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){[port]="wrong"}},"错误类型拒绝");
            foreach(var bad in new[]{new Line2D(100,100,100,100),new Line2D(double.NaN,100,200,200),new Line2D(-1,100,200,200)})
                Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){[port]=bad}},"退化/非有限/越界线拒绝");
        }
        Reject(c with{InputFrames=null},"无来源拒绝");
        Reject(c with{Node=c.Node with{Parameters=new(){{"taskRoi",J("0,0,120,120")}}}},"任务区域必须覆盖完整线段");
        using(var region=new HRegion(0d,0d,150d,500d))using(var reduced=image.ReduceDomain(region))Reject(Context(reduced,cases[0].Item1,cases[0].Item2),"无效图像域拒绝");
        using(var multi=image.ConcatObj(image))Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){["image"]=multi}},"多幅图拒绝");
        Reject(c with{Node=c.Node with{Parameters=new(){{"showLines",J("wrong")}}}},"错误绘图参数拒绝");
        for(var i=0;i<100;i++)
        {
            var result=Execute(Context(image,i%2==0?cases[0].Item1:null,cases[0].Item2));
            try{Check(result.Ports["measurement"].ValueKind==(i%2==0?JsonValueKind.Object:JsonValueKind.Null),"交替重跑清理旧几何 "+i);}finally{Release(result);}
        }
        var hidden=Execute(c with{Node=c.Node with{Parameters=new(){{"showLines",J(false)},{"showConnector",J(false)}}}});
        try{var overlay=(LinePairOverlay)hidden.RuntimePorts!["overlay"]!;Check(!overlay.ShowLines&&!overlay.ShowConnector&&hidden.Ports["segmentDistance"].GetDouble()==100,"绘图开关不改变测量");}finally{Release(hidden);}
        using(var rgb=image.Compose3(image,image)){var hash=Pixels(rgb);var result=Execute(Context(rgb,cases[0].Item1,cases[0].Item2));try{using var pixel=((HImage)result.RuntimePorts!["preview"]!).GetGrayval(150d,50d);Check(pixel.ToDArr().SequenceEqual(new double[]{255,210,0}),"独立RGB预览有真实间隙线");}finally{Release(result);}Check(Pixels(rgb)==hash,"共享RGB输入不污染");}
        Check(Pixels(image)==before,"灰度输入不污染");
        foreach(var atLog in new[]{false,true})using(var cancel=new CancellationTokenSource())
        {if(!atLog)cancel.Cancel();var caught=false;try{Release(Execute(c with{CancellationToken=cancel.Token,Log=_=>{if(atLog)cancel.Cancel();}}));}catch(OperationCanceledException){caught=true;}Check(caught,"取消不发布输出");}
        static bool On(Point2D point,Line2D line)=>HMisc.DistancePl(point.Row,point.Column,line.RowBegin,line.ColumnBegin,line.RowEnd,line.ColumnEnd)<1e-7&&point.Row>=Math.Min(line.RowBegin,line.RowEnd)-1e-7&&point.Row<=Math.Max(line.RowBegin,line.RowEnd)+1e-7&&point.Column>=Math.Min(line.ColumnBegin,line.ColumnEnd)-1e-7&&point.Column<=Math.Max(line.ColumnBegin,line.ColumnEnd)+1e-7;
    }
    private static FlowDocument Fixture()
    {
        using(var black=new HImage())
        {
            black.GenImageConst("byte",512,384);using var top=new HRegion(90d,30d,110d,480d);using var bottom=new HRegion(240d,30d,260d,480d);
            using var first=black.PaintRegion(top,220d,"fill");using var second=first.PaintRegion(bottom,220d,"fill");using var blurred=second.GaussFilter(3);
            blurred.WriteImage("png",0,Path.Combine(Root,"line-pair.png"));black.WriteImage("png",0,Path.Combine(Root,"blank.png"));
        }
        var source=new NodeInstance{TypeId=HalconNodeProvider.ImageRead,DisplayName="读取图像",X=20,Y=145,Parameters=new(){{"path",J(Path.Combine(Root,"line-pair.png"))}}};
        NodeInstance Line(string name,double y,string roi)=>new(){TypeId=HalconNodeProvider.StraightLineFind,DisplayName=name,X=300,Y=y,Parameters=new(){{"roi",J(roi)},{"lineOrientation",J("horizontal")},{"measureTransition",J("all")}}};
        var a=Line("直线 A",25,"60,40,105,470");var b=Line("直线 B",300,"210,40,255,470");var measure=Measure();
        var ra=new InspectionRegion(Guid.NewGuid(),"直线 A 范围",source.Id,"image",new(60,40,105,470));var rb=new InspectionRegion(Guid.NewGuid(),"直线 B 范围",source.Id,"image",new(210,40,255,470));var rm=new InspectionRegion(Guid.NewGuid(),"线线测量范围",source.Id,"image",new(50,20,280,490));
        InspectionTask Find(string name,NodeInstance n,InspectionRegion region)=>new(Guid.NewGuid(),name,region.Id,[n.Id],[new(Guid.NewGuid(),n.Id,"status",InspectionRuleKind.TextEquals,"/Code",Expected:LineFindStatus.Found)]);
        var tasks=new List<InspectionTask>{Find("直线 A 存在",a,ra),Find("直线 B 存在",b,rb),new(Guid.NewGuid(),"间距与平行度",rm.Id,[measure.Id],[new(Guid.NewGuid(),measure.Id,"segmentDistance",InspectionRuleKind.NumberRange,Minimum:149,Maximum:151),new(Guid.NewGuid(),measure.Id,"angle",InspectionRuleKind.NumberRange,Minimum:0,Maximum:1)])};
        return new(){SchemaVersion=2,Name="线线尺寸检查",Nodes=[source,a,b,measure],Edges=[new(Guid.NewGuid(),source.Id,"image",a.Id,"image"),new(Guid.NewGuid(),source.Id,"image",b.Id,"image"),new(Guid.NewGuid(),source.Id,"image",measure.Id,"image"),new(Guid.NewGuid(),a.Id,"line",measure.Id,"lineA"),new(Guid.NewGuid(),b.Id,"line",measure.Id,"lineB")],InspectionRegions=[ra,rb,rm],InspectionTasks=tasks,DetectionPanels=[new(Guid.NewGuid(),source.Id,Contents:[new(Guid.NewGuid(),source.Id,Overlays:[new(measure.Id,Fields:["angle","segmentDistance"])])])]};
    }
    private static void Pipeline()
    {
        var flow=Fixture();var measure=flow.Nodes.Last();using var camera=new HalconCameraManager();var registry=BuiltInNodeCatalog.Create();
        registry.Register(new HalconNodeProvider(),[new HalconImageReadExecutor(),new HalconCameraCaptureExecutor(camera),new HalconThresholdExecutor(),new HalconBlobAnalysisExecutor(),new HalconStraightLineFindExecutor(),new HalconSemanticSegmentationExecutor(),new HalconClassificationExecutor(),new HalconObjectDetectionExecutor(),new HalconQuickMatchExecutor(),new HalconPositionCorrectionExecutor()]);
        registry.Register(new HalconCircleNodeProvider(),[new HalconCircleFindExecutor()]);registry.Register(new HalconLinePairNodeProvider(),[new HalconLinePairExecutor()]);var validator=new WorkflowGraphValidator(registry);var engine=new WorkflowExecutionEngine(registry,validator);
        Check(validator.Validate(flow).Count==0,"真实四节点及任务图校验");
        using(var run=engine.RunAsync(flow).GetAwaiter().GetResult())
        {
            Check(run.Status==WorkflowRunStatus.Success&&run.Inspection?.Verdict==InspectionVerdict.Ok,"实际读图→两次找线→测量→三任务OK");
            var result=run.NodeResults.Single(n=>n.NodeId==measure.Id);var m=result.Outputs["measurement"].Deserialize<LinePairMeasurement>()!;
            File.WriteAllText(Path.Combine(Root,"actual-result.json"),JsonSerializer.Serialize(run.NodeResults,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"实际拟合：距离 {m.SegmentDistance:R} px，夹角 {m.AngleDegrees:R} 度");
            Check(Math.Abs(m.SegmentDistance-150)<.25&&m.AngleDegrees<.1,"实际Metrology测量接近名义150像素平行栅格尺寸");Observations.Add(new{pipeline=m});
            Check(run.SpatialOutputs![measure.Id]["measurement"].FrameId==run.SpatialOutputs[flow.Nodes[0].Id]["image"].FrameId,"测量结果保持原图帧身份");
            var scene=DetectionOverlayScene.Create(new(flow,registry,validator),run,flow.DetectionPanels[0].Contents![0],512,384);
            Check(scene.Geometry.Any(g=>g.Color=="Gold")&&scene.Geometry.Any(g=>g.Color=="DeepSkyBlue")&&scene.Captions.Any(c=>c.Text.Contains("线段最短距离")),"同图叠加显示两条线、间隙线及数值");
            File.WriteAllText(Path.Combine(Root,"actual-result.json"),JsonSerializer.Serialize(run.NodeResults,new JsonSerializerOptions{WriteIndented=true}));
            var displayFlow=new FlowTabViewModel(flow,registry,validator);
            using(var view=new DetectionContentView(new(Guid.NewGuid(),measure.Id,View:"检测结果"),()=>displayFlow,()=>run))
            {
                var grid=(DataGrid)typeof(DetectionContentView).GetField("_values",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
                var fields=grid.Items.Cast<NodeOutputValue>().Select(v=>v.Name).ToArray();
                Check(new[]{"angle","segmentDistance"}.All(fields.Contains),"检测结果表包含线线距离及夹角");
            }
            var mt=flow.InspectionTasks.Last();var exact=mt with{Rules=[mt.Rules[0] with{Minimum=m.SegmentDistance,Maximum=m.SegmentDistance}]};Check(ProductInspectionEvaluator.Evaluate(flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),exact]},run.Id,run.Status,run.NodeResults,run.Errors).Verdict==InspectionVerdict.Ok,"公差边界用原精度闭区间");
        }
        var task=flow.InspectionTasks.Last();var ng=flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),task with{Rules=[task.Rules[0] with{Maximum=10,Minimum=0}]}]};
        using(var run=engine.RunAsync(ng).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Ng,"故意超差为NG而非执行成功即OK");
        var source=flow.Nodes[0];var blank=flow with{Nodes=flow.Nodes.Select(n=>n.Id==source.Id?n with{Parameters=new(){{"path",J(Path.Combine(Root,"blank.png"))}}}:n).ToList()};
        using(var run=engine.RunAsync(blank).GetAwaiter().GetResult()){var result=run.NodeResults.Single(n=>n.NodeId==measure.Id);Check(run.Inspection?.Verdict==InspectionVerdict.Error&&result.Outputs["measurement"].ValueKind==JsonValueKind.Null,"空图失测为ERROR，无假零或旧几何");}
        var second=source with{Id=Guid.NewGuid(),DisplayName="独立读取"};var mixed=flow with{InspectionTasks=[],InspectionRegions=[],Nodes=[..flow.Nodes,second],Edges=flow.Edges.Select(e=>e.ToNodeId==flow.Nodes[2].Id?e with{FromNodeId=second.Id}:e).ToList()};
        using(var run=engine.RunAsync(mixed).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Failed&&run.NodeResults.Single(n=>n.NodeId==measure.Id).Error!.Contains("FRAME_MISMATCH"),"同一图片独立读取的跨帧几何被阻断");
        var wrong=flow with{Edges=flow.Edges.Select(e=>e.ToNodeId==measure.Id&&e.ToPortId=="image"?e with{FromNodeId=flow.Nodes[1].Id,FromPortId="preview"}:e).ToList()};Check(validator.Validate(wrong).Any(i=>i.Message.Contains("标注")),"标注预览接入原图在设计时拒绝");
        var store=new ProjectStore();var project=store.CreateProject(Path.Combine(Root,"线线尺寸检查.hvproj"),"线线尺寸检查");store.SaveProject(project,[flow]);var loaded=store.OpenProject(project.ProjectFilePath!);Check(loaded.Project.Flows.Single().Nodes.Count==4&&loaded.Project.Flows.Single().InspectionTasks.Count==3,"方案保存恢复四节点及三任务");store.SaveProject(project with{ProjectFilePath=Path.Combine(Root,"线线尺寸NG检查.hvproj")},[ng]);
        for(var i=0;i<15;i++){using var run=engine.RunAsync(flow).GetAwaiter().GetResult();Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"重复完整运行 "+i);var images=run.RuntimeOutputs!.Values.SelectMany(v=>v.Values).OfType<HImage>().ToArray();run.Dispose();Check(images.All(x=>!x.IsInitialized()),"每轮运行结果释放 "+i);}
    }
    private static void Ui()
    {
        var app=new App();app.InitializeComponent();var window=new MainWindow(Path.Combine(Root,"线线尺寸检查.hvproj"));
        try
        {
            window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);var vm=(WorkflowWorkspaceViewModel)window.DataContext;
            Check(vm.NodeCatalog.Any(n=>n.TypeId==HalconLinePairNodeProvider.LinePair),"真实WPF节点库注册线线测量");var flow=vm.SelectedFlow!;var selected=flow.Nodes.Single(n=>n.TypeId==HalconLinePairNodeProvider.LinePair);flow.SelectNode(selected.Id,false);window.UpdateLayout();
            foreach(var width in new[]{1180d,1366d,1580d}){var root=(FrameworkElement)window.Content;root.Measure(new Size(width,680));root.Arrange(new Rect(0,0,width,680));root.UpdateLayout();Check(selected.PrimaryParameters.Count()==2,"仅两个绘图开关，公差配置复用任务");}
            var editor=new InspectionTaskEditorWindow(window,flow,vm.NodeCatalog,null);try{editor.Show();editor.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);((ListBox)editor.FindName("TaskList")).SelectedIndex=2;typeof(InspectionTaskEditorWindow).GetMethod("AddRule_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(editor,[editor,new RoutedEventArgs()]);var rule=editor.Tasks.Last().Rules.Last();Check(rule.PortId=="segmentDistance"&&rule.Kind==InspectionRuleKind.NumberRange&&rule.Pointer=="","任务编辑器默认创建线线数值公差");}finally{editor.Close();}
            vm.SaveAll();Check(new ProjectStore().OpenProject(Path.Combine(Root,"线线尺寸检查.hvproj")).Project.Flows.Single().Edges.Count==5,"WPF保存恢复全部接线");
        }finally{window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
}
