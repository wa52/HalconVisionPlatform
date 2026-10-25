using System.IO;
using System.Reflection;
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
    private static string Root="";
    private static readonly List<string> Checks=[];
    private static JsonElement J<T>(T value)=>JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);Checks.Add(label);}
    private static NodeRegistry Registry()
    {
        var registry=BuiltInNodeCatalog.Create();
        registry.Register(new PipelineDefinitions(),[new HalconImageReadExecutor(),new HalconStraightLineFindExecutor()]);
        registry.Register(new HalconLinePairNodeProvider(),[new HalconLinePairExecutor()]);
        registry.Register(new HalconUnitConversionNodeProvider(),[new HalconPixelScaleExecutor(),new HalconLengthToMillimeterExecutor()]);return registry;
    }
    private sealed class PipelineDefinitions:INodeDefinitionProvider
    {public IEnumerable<NodeDefinition> GetDefinitions()=>new HalconNodeProvider().GetDefinitions().Where(d=>d.TypeId is HalconNodeProvider.ImageRead or HalconNodeProvider.StraightLineFind);}
    private static NodeInstance New(string type,string name,double x=0,double y=0)
    {
        var definition=Registry().Definitions.Single(d=>d.TypeId==type);
        return new(){TypeId=type,DisplayName=name,X=x,Y=y,Parameters=definition.Parameters.Where(p=>p.DefaultValue is not null).ToDictionary(p=>p.Id,p=>p.DefaultValue!.Value)};
    }
    private static NodeInstance Scale()=>New(HalconUnitConversionNodeProvider.ScaleCalibration,"比例标定",20,450) with{Parameters=new(){{"referencePixels",J(100d)},{"referenceMillimeters",J(10d)},{"imageWidth",J(512)},{"imageHeight",J(384)},{"confirmed",J(true)}}};
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0]))return 2;Root=Path.GetFullPath(args[0]);Directory.CreateDirectory(Root);
        try{Core();Pipeline();Ui();File.WriteAllText(Path.Combine(Root,"summary.json"),JsonSerializer.Serialize(new{count=Checks.Count,Checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {Checks.Count} 项比例标定/单位换算检查");return 0;}
        catch(Exception error){File.WriteAllText(Path.Combine(Root,"error.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    private static NodeExecutionContext Context(NodeInstance node,Dictionary<string,object?> inputs,FrameReference frame)=>new(node,new Dictionary<string,JsonElement>(),default,_=>{},inputs,frame.RunId,inputs.Keys.ToDictionary(key=>key,_=>frame));
    private static NodeExecutionOutput Execute(NodeExecutionContext c)=>c.Node.TypeId==HalconUnitConversionNodeProvider.ScaleCalibration?new HalconPixelScaleExecutor().ExecuteAsync(c).GetAwaiter().GetResult():new HalconLengthToMillimeterExecutor().ExecuteAsync(c).GetAwaiter().GetResult();
    private static void Reject(NodeExecutionContext c,string label){var rejected=false;try{Execute(c);}catch(InvalidDataException){rejected=true;}Check(rejected,label);}
    private static void Core()
    {
        using var image=new HImage();image.GenImageConst("byte",512,384);
        var frame=new FrameReference(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"image",Guid.NewGuid(),512,384,1,false,false,"fixture");
        var c=Context(Scale(),new(){{"image",image}},frame);var output=Execute(c);var scale=(PixelScaleCalibration)output.RuntimePorts!["calibration"]!;
        Check(scale.MillimetersPerPixel==.1&&output.RuntimePorts.Values.OfType<HObject>().Count()==0,"100px/10mm比例，不持有原图输出");
        Reject(c with{Node=New(HalconUnitConversionNodeProvider.ScaleCalibration,"未配置")},"默认标定拒绝输出有效比例");
        foreach(var key in new[]{"referencePixels","referenceMillimeters","imageWidth","imageHeight"})foreach(var value in new[]{0d,-1d})
            Reject(c with{Node=c.Node with{Parameters=new(c.Node.Parameters){[key]=J(value)}}},"非正标定参数拒绝："+key);
        Reject(c with{Node=c.Node with{Parameters=new(c.Node.Parameters){["confirmed"]=J(false)}}},"未确认成像条件拒绝");
        Reject(c with{Node=c.Node with{Parameters=new(c.Node.Parameters){["referencePixels"]=J(double.Epsilon)}}},"比例计算溢出拒绝");
        Reject(c with{Node=c.Node with{Parameters=new(c.Node.Parameters){["referenceMillimeters"]=J(double.Epsilon)}}},"比例计算下溢拒绝");
        Check(DetectionOverlayScene.FormatDisplayValue("0.000015")=="1.5E-05" || DetectionOverlayScene.FormatDisplayValue("0.000015")=="1.5E-05".ToLowerInvariant(),"很小的毫米值不显示为零");

        Reject(c with{Node=c.Node with{Parameters=new(c.Node.Parameters){["imageWidth"]=J(256)}}},"尺寸变更不能沿用比例");
        Reject(c with{Node=c.Node with{Parameters=new(c.Node.Parameters){["referencePixels"]=J(1000)}}},"不可能的参考线长度拒绝");
        using(var multi=image.ConcatObj(image))Reject(c with{RuntimeInputs=new Dictionary<string,object?>{{"image",multi}}},"多幅原图拒绝标定");
        foreach(var change in Changes())Reject(c with{InputFrames=new Dictionary<string,FrameReference>{{"image",change(frame)}}},"无效标定帧拒绝");
        var convert=Context(New(HalconUnitConversionNodeProvider.LengthConversion,"毫米长度"),new(){{"pixels",150d},{"calibration",scale}},frame);
        foreach(var pixels in new[]{150d,0d,-20d})
        {
            var result=Execute(convert with{RuntimeInputs=new Dictionary<string,object?>(convert.RuntimeInputs!){["pixels"]=pixels}});
            Check(result.Ports["lengthMm"].GetDouble()==pixels*.1,"正数/零/有符号净距按比例转换");
            Check(result.RuntimePorts!.Values.OfType<HObject>().Count()==0&&image.IsInitialized(),"换算不拥有或释放上游图像");
        }
        foreach(var change in Changes().Concat(new Func<FrameReference,FrameReference>[] {f=>f with{FrameId=Guid.NewGuid()},f=>f with{SpaceId=Guid.NewGuid()},f=>f with{SourceNodeId=Guid.NewGuid()},f=>f with{SourcePortId="other"},f=>f with{Width=256}}))
            Reject(convert with{InputFrames=new Dictionary<string,FrameReference>(convert.InputFrames!){["pixels"]=change(frame)}},"换算拒绝跨帧/来源/空间/尺寸");
        foreach(var invalid in new[]{scale with{IsotropicConfirmed=false},scale with{MillimetersPerPixel=-1},scale with{MillimetersPerPixel=.2},scale with{Model="unknown"},scale with{Width=256}})
            Reject(convert with{RuntimeInputs=new Dictionary<string,object?>(convert.RuntimeInputs!){["calibration"]=invalid}},"无效或不一致标定拒绝");
        foreach(var value in new object[]{double.NaN,double.PositiveInfinity,double.Epsilon,"wrong"})
            Reject(convert with{RuntimeInputs=new Dictionary<string,object?>(convert.RuntimeInputs!){["pixels"]=value}},"非有限/下溢/错误类型拒绝");
        var large=scale with{ReferenceMillimeters=300,MillimetersPerPixel=3};
        Reject(convert with{RuntimeInputs=new Dictionary<string,object?>{{"pixels",double.MaxValue},{"calibration",large}}},"乘法溢出拒绝");
        for(var i=0;i<100;i++)
        {
            var result=Execute(convert with{Inputs=new Dictionary<string,JsonElement>{{"pixels",J(999d)}},RuntimeInputs=new Dictionary<string,object?>(convert.RuntimeInputs!){["pixels"]=i%2==0?150d:null}});
            Check(result.Ports["lengthMm"].ValueKind==(i%2==0?JsonValueKind.Number:JsonValueKind.Null),"交替失测不回用旧值 "+i);
        }
        foreach(var atLog in new[]{false,true})using(var cancel=new CancellationTokenSource())
        {if(!atLog)cancel.Cancel();var cancelled=false;try{Execute(convert with{CancellationToken=cancel.Token,Log=_=>{if(atLog)cancel.Cancel();}});}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"取消前/计算后无输出");}
        static Func<FrameReference,FrameReference>[] Changes()=>[f=>f with{RunId=Guid.NewGuid()},f=>f with{Count=2},f=>f with{ImageIndex=2},f=>f with{Annotated=true},f=>f with{Corrected=true},f=>f with{SourcePortId=""}];
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
        var a=Line("直线 A",25,"60,40,105,470");var b=Line("直线 B",300,"210,40,255,470");var measure=New(HalconLinePairNodeProvider.LinePair,"线线测量",650,80);
        var ra=new InspectionRegion(Guid.NewGuid(),"直线 A 范围",source.Id,"image",new(60,40,105,470));var rb=new InspectionRegion(Guid.NewGuid(),"直线 B 范围",source.Id,"image",new(210,40,255,470));var rm=new InspectionRegion(Guid.NewGuid(),"线线测量范围",source.Id,"image",new(50,20,280,490));
        InspectionTask Find(string name,NodeInstance n,InspectionRegion region)=>new(Guid.NewGuid(),name,region.Id,[n.Id],[new(Guid.NewGuid(),n.Id,"status",InspectionRuleKind.TextEquals,"/Code",Expected:LineFindStatus.Found)]);
        var tasks=new List<InspectionTask>{Find("直线 A 存在",a,ra),Find("直线 B 存在",b,rb),new(Guid.NewGuid(),"间距与平行度",rm.Id,[measure.Id],[new(Guid.NewGuid(),measure.Id,"segmentDistance",InspectionRuleKind.NumberRange,Minimum:149,Maximum:151),new(Guid.NewGuid(),measure.Id,"angle",InspectionRuleKind.NumberRange,Minimum:0,Maximum:1)])};
        return new(){SchemaVersion=2,Name="线线尺寸检查",Nodes=[source,a,b,measure],Edges=[new(Guid.NewGuid(),source.Id,"image",a.Id,"image"),new(Guid.NewGuid(),source.Id,"image",b.Id,"image"),new(Guid.NewGuid(),source.Id,"image",measure.Id,"image"),new(Guid.NewGuid(),a.Id,"line",measure.Id,"lineA"),new(Guid.NewGuid(),b.Id,"line",measure.Id,"lineB")],InspectionRegions=[ra,rb,rm],InspectionTasks=tasks,DetectionPanels=[new(Guid.NewGuid(),source.Id,Contents:[new(Guid.NewGuid(),source.Id,Overlays:[new(measure.Id,Fields:["angle","segmentDistance"])])])]};
    }

    private static FlowDocument CalibratedFixture()
    {
        var flow=Fixture();var source=flow.Nodes[0];var measure=flow.Nodes.Last();var scale=Scale();var convert=New(HalconUnitConversionNodeProvider.LengthConversion,"毫米间距",950,180);
        var task=flow.InspectionTasks.Last();
        return flow with{Name="合成图毫米换算检查",Nodes=[..flow.Nodes,scale,convert],Edges=[..flow.Edges,new(Guid.NewGuid(),source.Id,"image",scale.Id,"image"),new(Guid.NewGuid(),measure.Id,"segmentDistance",convert.Id,"pixels"),new(Guid.NewGuid(),scale.Id,"calibration",convert.Id,"calibration")],
            InspectionTasks=[..flow.InspectionTasks.Take(2),task with{NodeIds=[convert.Id,measure.Id],Rules=[new(Guid.NewGuid(),convert.Id,"lengthMm",InspectionRuleKind.NumberRange,Minimum:14.9,Maximum:15.1),task.Rules[1]]}],
            DetectionPanels=[new(Guid.NewGuid(),source.Id,Contents:[new(Guid.NewGuid(),source.Id,Overlays:[new(measure.Id,Fields:["angle","segmentDistance"]),new(convert.Id,Fields:["lengthMm"])])])]};
    }
    private static void Pipeline()
    {
        var flow=CalibratedFixture();var registry=Registry();var validator=new WorkflowGraphValidator(registry);var engine=new WorkflowExecutionEngine(registry,validator);var convert=flow.Nodes.Last();var scale=flow.Nodes[^2];var measure=flow.Nodes[3];
        Check(validator.Validate(flow).Count==0,"六节点毫米任务图合法");
        using(var run=engine.RunAsync(flow).GetAwaiter().GetResult())
        {
            Check(run.Status==WorkflowRunStatus.Success&&run.Inspection?.Verdict==InspectionVerdict.Ok,"实际读图/找线/测量/比例/换算/毫米公差OK");
            var result=run.NodeResults.Single(n=>n.NodeId==convert.Id);Check(result.Outputs["lengthMm"].GetDouble()==15,"合成图150px按示例比例换算15mm");
            Check(run.SpatialOutputs![measure.Id].ContainsKey("segmentDistance")&&!run.SpatialOutputs[measure.Id].ContainsKey("angle"),"仅声明长度单位的标量传递帧身份");
            File.WriteAllText(Path.Combine(Root,"actual-result.json"),JsonSerializer.Serialize(run.NodeResults,new JsonSerializerOptions{WriteIndented=true}));
            var displayFlow=new FlowTabViewModel(flow,registry,validator);
            var scene=DetectionOverlayScene.Create(displayFlow,run,flow.DetectionPanels[0].Contents![0],512,384);
            Check(scene.Captions.Any(c=>c.Text.Contains("mm")&&c.Text.Contains("15"))&&!scene.Captions.Any(c=>c.Text.Contains("不匹配")),"毫米标注与原图空间匹配");
            using var view=new DetectionContentView(new(Guid.NewGuid(),convert.Id,View:"检测结果"),()=>displayFlow,()=>run);
            var grid=(DataGrid)typeof(DetectionContentView).GetField("_values",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            Check(grid.Items.Cast<NodeOutputValue>().Any(v=>v.Name=="lengthMm"),"检测结果表包含毫米数值");
        }
        var task=flow.InspectionTasks.Last();var ng=flow with{InspectionTasks=[..flow.InspectionTasks.Take(2),task with{Rules=[task.Rules[0] with{Minimum=0,Maximum=14},task.Rules[1]]}]};
        using(var run=engine.RunAsync(ng).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Ng,"毫米公差超限为NG");
        var blank=flow with{Nodes=flow.Nodes.Select(n=>n.Id==flow.Nodes[0].Id?n with{Parameters=new(){{"path",J(Path.Combine(Root,"blank.png"))}}}:n).ToList()};
        using(var run=engine.RunAsync(blank).GetAwaiter().GetResult())
        {
            Check(run.Inspection?.Verdict==InspectionVerdict.Error&&run.NodeResults.Single(n=>n.NodeId==convert.Id).Outputs["lengthMm"].ValueKind==JsonValueKind.Null,"失测无毫米结果，产品ERROR");
            var missingScene=DetectionOverlayScene.Create(new(blank,registry,validator),run,blank.DetectionPanels[0].Contents![0],512,384);
            Check(!missingScene.Captions.Any(c=>c.Text.Contains("不匹配")),"纯数值失测不误报图形尺寸错误");
        }
        var unconfigured=flow with{Nodes=flow.Nodes.Select(n=>n.Id==scale.Id?n with{Parameters=new(n.Parameters){["confirmed"]=J(false)}}:n).ToList()};
        using(var run=engine.RunAsync(unconfigured).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Failed&&run.Inspection?.Verdict==InspectionVerdict.Error&&run.NodeResults.Single(n=>n.NodeId==convert.Id).Status==NodeExecutionStatus.Skipped,"未确认标定阻断换算，不能放行");
        var wrong=flow with{Edges=flow.Edges.Select(e=>e.ToNodeId==convert.Id&&e.ToPortId=="pixels"?e with{FromPortId="angle"}:e).ToList()};Check(validator.Validate(wrong).Any(i=>i.Message.Contains("单位")),"角度不能误接像素长度");
        var score=flow with{Edges=flow.Edges.Select(e=>e.ToNodeId==convert.Id&&e.ToPortId=="pixels"?e with{FromNodeId=flow.Nodes[1].Id,FromPortId="score"}:e).ToList()};
        Check(validator.Validate(score).Any(i=>i.Message.Contains("单位")),"无单位评分不可转毫米");
        var mmAgain=flow with{Edges=[..flow.Edges,new(Guid.NewGuid(),convert.Id,"lengthMm",convert.Id,"pixels")]};Check(validator.Validate(mmAgain).Any(i=>i.Message.Contains("单位")),"毫米不可二次误接为像素");
        var second=flow.Nodes[0] with{Id=Guid.NewGuid(),DisplayName="另一采集"};var mixed=flow with{InspectionTasks=[],InspectionRegions=[],Nodes=[..flow.Nodes,second],Edges=flow.Edges.Select(e=>e.ToNodeId==scale.Id?e with{FromNodeId=second.Id}:e).ToList()};
        using(var run=engine.RunAsync(mixed).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Failed&&run.NodeResults.Single(n=>n.NodeId==convert.Id).Error!.Contains("FRAME_MISMATCH"),"相同文件独立采集也拒绝换算");
        var store=new ProjectStore();var project=store.CreateProject(Path.Combine(Root,"合成图毫米换算检查.hvproj"),"合成图毫米换算检查");store.SaveProject(project,[flow]);
        var restored=store.OpenProject(project.ProjectFilePath!);Check(restored.Project.Flows.Single().Nodes.Count==6&&restored.Project.Flows.Single().Nodes[^2].Parameters["confirmed"].GetBoolean(),"标定参数、单位节点和毫米公差保存恢复");
        store.SaveProject(project with{ProjectFilePath=Path.Combine(Root,"未确认标定检查.hvproj")},[unconfigured]);
        for(var i=0;i<20;i++){using var run=engine.RunAsync(flow).GetAwaiter().GetResult();Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"完整链路重复运行 "+i);var images=run.RuntimeOutputs!.Values.SelectMany(o=>o.Values).OfType<HObject>().ToArray();run.Dispose();Check(images.All(o=>!o.IsInitialized()),"图像所有权释放 "+i);}
    }
    private static void Ui()
    {
        var app=new App();app.InitializeComponent();var window=new MainWindow(Path.Combine(Root,"合成图毫米换算检查.hvproj"));window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            var vm=(WorkflowWorkspaceViewModel)window.DataContext;Check(vm.NodeCatalog.Count(n=>n.Category=="标定与单位")==2,"节点库两个职责清晰的标定/换算节点");var flow=vm.SelectedFlow!;
            foreach(var width in new[]{1180d,1366d,1580d}){var root=(FrameworkElement)window.Content;flow.SelectNode(flow.Nodes.Single(n=>n.TypeId==HalconUnitConversionNodeProvider.ScaleCalibration).Id,false);root.Measure(new Size(width,680));root.Arrange(new Rect(0,0,width,680));root.UpdateLayout();Check(flow.SelectedNode!.PrimaryParameters.Count()==5,"标定五项参数可配置："+width);}
            var node=flow.Nodes.Single(n=>n.TypeId==HalconUnitConversionNodeProvider.LengthConversion);Check(node.InputPorts.Single(p=>p.Id=="pixels").ConnectionHelp.Contains("px"),"端口提示包含单位");
            var editor=new InspectionTaskEditorWindow(window,flow,vm.NodeCatalog,null);try{editor.Show();editor.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);((ListBox)editor.FindName("TaskList")).SelectedIndex=2;typeof(InspectionTaskEditorWindow).GetMethod("AddRule_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(editor,[editor,new RoutedEventArgs()]);Check(editor.Tasks.Last().Rules.Last().PortId=="lengthMm","任务仍支持选成员的毫米公差");}finally{editor.Close();}
            vm.SaveAll();
        }finally{window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
}
