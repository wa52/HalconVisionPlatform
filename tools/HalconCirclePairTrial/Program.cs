using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private static string Root = "";
    private static NodeDefinition Definition => new HalconCirclePairNodeProvider().GetDefinitions().Single();
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Checks.Add(label); }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static NodeInstance Measure() => new() { TypeId = HalconCirclePairNodeProvider.CirclePair, DisplayName = "圆圆测量", X = 690, Y = 130, Parameters = Definition.Parameters.ToDictionary(p => p.Id, p => p.DefaultValue!.Value) };
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0]) || File.Exists(args[0])) return 2;
        Root = Path.GetFullPath(args[0]); Directory.CreateDirectory(Root);
        try { Core(); Pipeline(); Ui(); Save(); Console.WriteLine($"PASS {Checks.Count} 项圆圆测量检查"); return 0; }
        catch (Exception error) { Save(); File.WriteAllText(Path.Combine(Root,"error.txt"),error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static void Save() => File.WriteAllText(Path.Combine(Root,"summary.json"),JsonSerializer.Serialize(new{count=Checks.Count,Checks,Measurements},new JsonSerializerOptions{WriteIndented=true}));
    private static NodeExecutionContext Context(HImage image, Circle2D? a, Circle2D? b, NodeInstance? node = null)
    {
        image.GetImageSize(out int w,out int h); var run=Guid.NewGuid();
        var frame=new FrameReference(run,Guid.NewGuid(),Guid.NewGuid(),"image",Guid.NewGuid(),w,h,1,false,false,"fixture");
        return new(node??Measure(),new Dictionary<string,JsonElement>(),default,_=>{},
            new Dictionary<string,object?>{{"image",image},{"circleA",a},{"circleB",b}},run,
            new Dictionary<string,FrameReference>{{"image",frame},{"circleA",frame},{"circleB",frame}});
    }
    private static NodeExecutionOutput Execute(NodeExecutionContext c) => new HalconCirclePairExecutor().ExecuteAsync(c).GetAwaiter().GetResult();
    private static void Release(NodeExecutionOutput output)
    { var images=output.RuntimePorts!.Values.OfType<HImage>().ToArray();foreach(var image in images)image.Dispose();Check(images.All(i=>!i.IsInitialized()),"本次测量预览释放"); }
    private static void Reject(NodeExecutionContext c,string label)
    {var rejected=false;try{var output=Execute(c);Release(output);}catch(InvalidDataException){rejected=true;}Check(rejected,label);}
    private static string Pixels(HImage image)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var channels=image.CountChannels();
        for(var i=1;i<=channels.I;i++){using var plane=image.AccessChannel(i);var pointer=plane.GetImagePointer1(out string type,out int w,out int h);var bytes=new byte[w*h*(type=="byte"?1:type=="uint2"?2:4)];Marshal.Copy(pointer,bytes,0,bytes.Length);hash.AppendData(bytes);}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Core()
    {
        using var image=new HImage();image.GenImageConst("byte",512,512);var original=Pixels(image);
        var cases=new[]{
            (new Circle2D(200,140,30),new Circle2D(200,340,20),200d,150d,"相离"),
            (new Circle2D(200,140,30),new Circle2D(260,220,20),100d,50d,"相离"),
            (new Circle2D(200,200,50),new Circle2D(200,240,40),40d,0d,"相交"),
            (new Circle2D(200,200,70),new Circle2D(200,210,20),10d,40d,"包含"),
            (new Circle2D(200,210,20),new Circle2D(200,200,70),10d,40d,"包含"),
            (new Circle2D(200,200,70),new Circle2D(200,200,20),0d,50d,"同心包含"),
            (new Circle2D(200,200,30),new Circle2D(200,200,30),0d,0d,"重合"),
            (new Circle2D(200,200,30),new Circle2D(200,250,20),50d,0d,"外切"),
            (new Circle2D(200,200,70),new Circle2D(200,250,20),50d,0d,"内切")};
        foreach(var(a,b,distance,gap,relation)in cases)
        {
            var output=Execute(Context(image,a,b));
            try
            {
                var m=(CirclePairMeasurement)output.RuntimePorts!["measurement"]!;
                Check(Math.Abs(m.CenterDistance-distance)<1e-9&&Math.Abs(m.BoundaryDistance-gap)<1e-9&&m.Relation==relation,"HALCON 圆心距及解析圆周距离："+relation);
                Check(m.BoundaryLine is null ? gap==0 : Math.Abs(m.BoundaryLine.Length-gap)<1e-9,"最短连线长度与圆周距离一致："+relation);
                if(m.BoundaryLine is{} line)Check(Math.Abs(HMisc.DistancePp(line.RowBegin,line.ColumnBegin,a.Row,a.Column)-a.Radius)<1e-9&&Math.Abs(HMisc.DistancePp(line.RowEnd,line.ColumnEnd,b.Row,b.Column)-b.Radius)<1e-9,"最短连线端点分别位于两个圆周");
                Check(output.Ports["status"].Deserialize<CirclePairStatus>()!.Code==CirclePairStatus.Measured,"测量完成状态不等同公差 OK");
                Measurements.Add(m);
            }finally{Release(output);}
            var reverse=Execute(Context(image,b,a));try{Check(Math.Abs(reverse.Ports["centerDistance"].GetDouble()-distance)<1e-9&&Math.Abs(reverse.Ports["boundaryDistance"].GetDouble()-gap)<1e-9,"交换圆输入不改变两种距离");}finally{Release(reverse);}
        }
        Check(Pixels(image)==original,"测量与绘图不改变原始灰度像素");
        using var rgb=image.Compose3(image,image);var colored=Execute(Context(rgb,cases[0].Item1,cases[0].Item2));
        try{using var pixel=((HImage)colored.RuntimePorts!["preview"]!).GetGrayval(200d,230d);Check(pixel.ToDArr().SequenceEqual(new double[]{255,210,0}),"预览真实显示最短距离金色线");}finally{Release(colored);}
        using(var expectedRgb=image.Compose3(image,image))Check(Pixels(rgb)==Pixels(expectedRgb),"共享 RGB 输入像素未污染");
        var c=Context(image,cases[0].Item1,cases[0].Item2);
        foreach(var change in new Func<FrameReference,FrameReference>[] {f=>f with{RunId=Guid.NewGuid()},f=>f with{FrameId=Guid.NewGuid()},f=>f with{SpaceId=Guid.NewGuid()},f=>f with{SourceNodeId=Guid.NewGuid()},f=>f with{SourcePortId="other"},f=>f with{Width=42},f=>f with{Height=42},f=>f with{Count=2},f=>f with{ImageIndex=2},f=>f with{Annotated=true},f=>f with{Corrected=true}})
            Reject(c with{InputFrames=new Dictionary<string,FrameReference>(c.InputFrames!){["circleB"]=change(c.InputFrames!["circleB"])}},"拒绝不同轮次/帧/空间/来源/尺寸/索引或标注的圆");
        Reject(c with{InputFrames=null},"无图像来源时拒绝计算");
        Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){["circleA"]="not-a-circle"}},"错误运行类型不能伪装成正常未检出");
        using(var multi=image.ConcatObj(image)){Reject(c with{RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){["image"]=multi}},"不接受多对象输入冒充单幅图像");}
        var noOrigin=new Dictionary<string,FrameReference>(c.InputFrames!);noOrigin.Remove("circleA");Reject(c with{InputFrames=noOrigin},"无圆来源时拒绝计算");
        foreach(var bad in new[]{new Circle2D(200,200,0),new Circle2D(200,200,-1),new Circle2D(double.NaN,200,20),new Circle2D(2,2,20)})Reject(Context(image,bad,cases[0].Item2),"拒绝非法半径/坐标或越界");
        var scoped=Measure() with{Parameters=new(){{"taskRoi",J("0,0,100,100")}}};Reject(Context(image,cases[0].Item1,cases[0].Item2,scoped),"两完整圆超出任务区域时不自动裁小");
        using(var domain=new HRegion(0d,0d,100d,100d))using(var reduced=image.ReduceDomain(domain))Reject(Context(reduced,cases[0].Item1,cases[0].Item2),"测量包围范围存在无效像素时拒绝");
        var missing=Execute(c with{Inputs=new Dictionary<string,JsonElement>{{"circleA",J(cases[0].Item1)}},RuntimeInputs=new Dictionary<string,object?>(c.RuntimeInputs!){["circleA"]=null}});
        try{Check(missing.Ports["centerDistance"].ValueKind==JsonValueKind.Null&&missing.Ports["measurement"].ValueKind==JsonValueKind.Null&&missing.Ports["status"].Deserialize<CirclePairStatus>()!.Code==CirclePairStatus.Missing,"空运行时圆不回退到旧 JSON 值，不输出假零或旧线");}finally{Release(missing);}
        using(var cancellation=new CancellationTokenSource()){cancellation.Cancel();var cancelled=false;try{Execute(c with{CancellationToken=cancellation.Token});}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"预取消不生成输出");}
        using(var cancellation=new CancellationTokenSource()){var cancelled=false;try{Execute(c with{CancellationToken=cancellation.Token,Log=_=>cancellation.Cancel()});}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"测量完成时取消不发布半帧预览");}
        for(var i=0;i<100;i++){var output=Execute(Context(image,i%2==0?cases[0].Item1:null,cases[0].Item2));try{Check(output.Ports["centerDistance"].ValueKind==(i%2==0?JsonValueKind.Number:JsonValueKind.Null),"连续有圆/缺圆不残留数据 "+i);}finally{Release(output);}}
    }
    private static NodeRegistry Registry()
    {
        var registry=BuiltInNodeCatalog.Create();registry.Register(new ImageDefinition(),[new HalconImageReadExecutor()]);
        registry.Register(new HalconCircleNodeProvider(),[new HalconCircleFindExecutor()]);registry.Register(new HalconCirclePairNodeProvider(),[new HalconCirclePairExecutor()]);return registry;
    }
    private sealed class ImageDefinition:INodeDefinitionProvider{public IEnumerable<NodeDefinition> GetDefinitions()=>new HalconNodeProvider().GetDefinitions().Where(n=>n.TypeId==HalconNodeProvider.ImageRead);}
    private static FlowDocument Fixture()
    {
        var folder=Path.Combine(Root,"images");Directory.CreateDirectory(folder);
        using(var image=new HImage())
        {
            image.GenImageConst("byte",512,320);using var background=image.ScaleImage(0d,30d);using var ca=new HRegion();ca.GenCircle(160d,140d,45d);using var cb=new HRegion();cb.GenCircle(160d,350d,35d);
            using var first=background.PaintRegion(ca,220d,"fill");using var both=first.PaintRegion(cb,220d,"fill");using var blurred=both.GaussFilter(3);blurred.WriteImage("png",0,Path.Combine(folder,"pair.png"));
            image.WriteImage("png",0,Path.Combine(folder,"blank.png"));
        }
        var source=new NodeInstance{TypeId=HalconNodeProvider.ImageRead,DisplayName="读取图像",X=30,Y=145,Parameters=new(){{"path",J(Path.Combine(folder,"pair.png"))}}};
        NodeInstance Circle(string name,double x,double y,double radius,string roi)=>new(){TypeId=HalconCircleNodeProvider.CircleFind,DisplayName=name,X=x,Y=y,Parameters=CircleParameters(radius,roi)};
        var a=Circle("圆 A",300,40,45,"100,80,220,200");var b=Circle("圆 B",300,265,35,"110,300,210,400");var measure=Measure();
        var ra=new InspectionRegion(Guid.NewGuid(),"圆 A 范围",source.Id,"image",new(100,80,220,200));var rb=new InspectionRegion(Guid.NewGuid(),"圆 B 范围",source.Id,"image",new(110,300,210,400));var rm=new InspectionRegion(Guid.NewGuid(),"尺寸测量范围",source.Id,"image",new(90,70,230,420));
        InspectionTask FindTask(string name,InspectionRegion r,NodeInstance n)=>new(Guid.NewGuid(),name,r.Id,[n.Id],[new(Guid.NewGuid(),n.Id,"status",InspectionRuleKind.TextEquals,"/Code",Expected:CircleFindStatus.Found)]);
        var mt=new InspectionTask(Guid.NewGuid(),"圆心距与间距公差",rm.Id,[measure.Id],[new(Guid.NewGuid(),measure.Id,"centerDistance",InspectionRuleKind.NumberRange,Minimum:209,Maximum:211),new(Guid.NewGuid(),measure.Id,"boundaryDistance",InspectionRuleKind.NumberRange,Minimum:129,Maximum:131)]);
        return new(){Name="圆圆尺寸检查",SchemaVersion=2,Nodes=[source,a,b,measure],Edges=[new(Guid.NewGuid(),source.Id,"image",a.Id,"image"),new(Guid.NewGuid(),source.Id,"image",b.Id,"image"),new(Guid.NewGuid(),source.Id,"image",measure.Id,"image"),new(Guid.NewGuid(),a.Id,"circle",measure.Id,"circleA"),new(Guid.NewGuid(),b.Id,"circle",measure.Id,"circleB")],InspectionRegions=[ra,rb,rm],InspectionTasks=[FindTask("圆 A 存在",ra,a),FindTask("圆 B 存在",rb,b),mt],DetectionPanels=[new(Guid.NewGuid(),source.Id,Contents:[new(Guid.NewGuid(),source.Id,Overlays:[new(measure.Id,Fields:["centerDistance","boundaryDistance"])])])]};
    }
    private static Dictionary<string,JsonElement> CircleParameters(double radius,string roi)
    {var p=new HalconCircleNodeProvider().GetDefinitions().Single().Parameters.ToDictionary(p=>p.Id,p=>p.DefaultValue!.Value);p["referenceRadius"]=J(radius);p["roi"]=J(roi);p["searchHalfWidth"]=J(6d);return p;}
    private static void Pipeline()
    {
        var registry=Registry();var validator=new WorkflowGraphValidator(registry);var engine=new WorkflowExecutionEngine(registry,validator);var flow=Fixture();var measure=flow.Nodes.Last();var source=flow.Nodes[0];
        Check(validator.Validate(flow).Count==0,"两个独立找圆任务及跨区域测量任务可合法接线");
        using(var run=engine.RunAsync(flow).GetAwaiter().GetResult())
        {
            Check(run.Status==WorkflowRunStatus.Success&&run.Inspection?.Verdict==InspectionVerdict.Ok,"真实读图→两次找圆→测量→三个必检任务 OK");
            var result=run.NodeResults.Single(n=>n.NodeId==measure.Id);var m=result.Outputs["measurement"].Deserialize<CirclePairMeasurement>()!;Measurements.Add(new{pipeline=m});
            Check(Math.Abs(m.CenterDistance-210)<.25&&Math.Abs(m.BoundaryDistance-130)<.5,"真实拟合后距离接近已知栅格尺寸");
            Check(run.SpatialOutputs![flow.Nodes[1].Id]["circle"].FrameId==run.SpatialOutputs[source.Id]["image"].FrameId,"圆几何沿引擎携带原图帧来源");
            Check(run.SpatialOutputs[measure.Id]["measurement"].SpaceId==run.SpatialOutputs[source.Id]["image"].SpaceId,"测量结果保留坐标空间");
            Check(NodeOutputValue.From(result,Definition).Any(v=>v.Name=="measurement.CenterLine.ColumnBegin"),"完整端点数据保留供后续计算");
            var scene=DetectionOverlayScene.Create(new(flow,registry,validator),run,flow.DetectionPanels[0].Contents![0],512,320);
            Check(scene.Geometry.Any(g=>g.Color=="DeepSkyBlue")&&scene.Geometry.Any(g=>g.Color=="Gold")&&scene.Captions.Any(c=>c.Text.Contains("圆心距")),"原图组合显示两种测量线及数值");
            var t=flow.InspectionTasks.Last();var exact=t with{Rules=[t.Rules[0] with{Minimum=m.CenterDistance,Maximum=m.CenterDistance}]};
            Check(ProductInspectionEvaluator.Evaluate(flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),exact]},run.Id,run.Status,run.NodeResults,run.Errors).Verdict==InspectionVerdict.Ok,"公差上下限采用闭区间且原始精度参与判定");
            File.WriteAllText(Path.Combine(Root,"actual-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        }
        var task=flow.InspectionTasks.Last();var ng=flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),task with{Rules=[task.Rules[0] with{Minimum=1,Maximum=10}]}]};
        using(var run=engine.RunAsync(ng).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Success&&run.Inspection?.Verdict==InspectionVerdict.Ng,"尺寸超差为 NG，算法成功不能误放行");
        var blank=flow with{Nodes=[source with{Parameters=new(){{"path",J(Path.Combine(Root,"images","blank.png"))}}},..flow.Nodes.Skip(1)]};
        using(var run=engine.RunAsync(blank).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Error&&run.NodeResults.Last().Outputs["centerDistance"].ValueKind==JsonValueKind.Null,"缺圆失测为 ERROR，未用零值参与公差");
        var other=source with{Id=Guid.NewGuid(),DisplayName="另一个采集"};var mixed=flow with{InspectionTasks=[],InspectionRegions=[],Nodes=[..flow.Nodes,other],Edges=flow.Edges.Select(e=>e.ToNodeId==flow.Nodes[2].Id?e with{FromNodeId=other.Id}:e).ToList()};
        using(var run=engine.RunAsync(mixed).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Failed&&run.NodeResults.Single(n=>n.NodeId==measure.Id).Error!.Contains("FRAME_MISMATCH"),"相同图片文件的两次独立采集仍拒绝跨帧测量");
        var wrong=flow with{Edges=flow.Edges.Select(e=>e.ToNodeId==measure.Id&&e.ToPortId=="image"?e with{FromNodeId=flow.Nodes[1].Id,FromPortId="preview"}:e).ToList()};
        Check(validator.Validate(wrong).Any(i=>i.Message.Contains("标注")),"设计时拒绝标注预览误接测量原图");
        var store=new ProjectStore();var project=store.CreateProject(Path.Combine(Root,"圆圆尺寸检查.hvproj"),"圆圆尺寸检查");store.SaveProject(project,[flow]);var loaded=store.OpenProject(project.ProjectFilePath!);
        Check(loaded.Project.Flows.Single().Nodes.Count==4&&loaded.Project.Flows.Single().Edges.Count==5&&loaded.Project.Flows.Single().InspectionTasks.Count==3,"单文件方案保存恢复节点、连线、三个任务及公差");
        store.SaveProject(project with{ProjectFilePath=Path.Combine(Root,"圆圆尺寸NG检查.hvproj")},[ng]);
        for(var i=0;i<20;i++){using var run=engine.RunAsync(flow).GetAwaiter().GetResult();Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"全链路重复测量 "+i);var objects=run.RuntimeOutputs!.Values.SelectMany(d=>d.Values).OfType<HImage>().ToArray();run.Dispose();Check(objects.All(o=>!o.IsInitialized()),"全链路图像所有权释放 "+i);}
    }
    private static void Ui()
    {
        var app=new App();app.InitializeComponent();var window=new MainWindow(Path.Combine(Root,"圆圆尺寸检查.hvproj"));window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            var vm=(WorkflowWorkspaceViewModel)window.DataContext;Check(vm.NodeCatalog.Count(n=>n.TypeId==HalconCirclePairNodeProvider.CirclePair)==1,"节点库唯一注册圆圆测量");var flow=vm.SelectedFlow!;var node=flow.Nodes.Single(n=>n.TypeId==HalconCirclePairNodeProvider.CirclePair);flow.SelectNode(node.Id,false);
            var catalog=(ListBox)window.FindName("NodeCatalogList");
            var grouped=(System.Windows.Data.ListCollectionView)catalog.ItemsSource;
            Check(grouped.Groups is { Count: > 1 }, "节点库按注册类别分组");
            vm.NodeSearchText="__no_match__";window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(catalog.Items.Count==0,"空搜索结果不保留旧节点");
            vm.NodeSearchText="";window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(catalog.Items.Count==vm.NodeCatalog.Count,"清空搜索恢复全部节点");
            Check(VirtualizingPanel.GetIsVirtualizingWhenGrouping(catalog)&&ScrollViewer.GetCanContentScroll(catalog),"分组保留列表虚拟化滚动");
            Check(node.InputPorts.All(p=>p.ConnectionHelp.Contains(p.Name)),"端口说明包含名称");
            Check(DetectionOverlayScene.FormatDisplayValue("209.99808525646353")=="209.998","图像标注三位小数且不改变输出精度");
            var root=(FrameworkElement)window.Content;
            foreach(var width in new[]{1180d,1366d,1580d}){root.Measure(new Size(width,680));root.Arrange(new Rect(0,0,width,680));root.UpdateLayout();Check(node.InputPorts.Count==3&&node.OutputPorts.Count==6&&node.PrimaryParameters.Count()==2,"紧凑类型端口及仅两个显示参数："+width);}
            var editor=new InspectionTaskEditorWindow(window,flow,vm.NodeCatalog,null);editor.Show();editor.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            try{((ListBox)editor.FindName("TaskList")).SelectedIndex=2;typeof(InspectionTaskEditorWindow).GetMethod("AddRule_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,[editor,new RoutedEventArgs()]);var rule=editor.Tasks.Last().Rules.Last();Check(rule.Kind==InspectionRuleKind.NumberRange&&rule.PortId=="centerDistance"&&rule.Pointer=="","新增测量公差复用数值范围，不误用快捷匹配状态");}finally{editor.Close();}
            vm.SaveAll();Check(File.Exists(Path.Combine(Root,"圆圆尺寸检查.hvproj")),"WPF 保存方案成功");
        }finally{window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
}
