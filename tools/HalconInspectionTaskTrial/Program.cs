using System.IO;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;
using HalconVision.App;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class Program
{
    private static readonly List<string> Passed=[];
    private static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);Passed.Add(name);}
    private static JsonElement J(object? value)=>JsonSerializer.SerializeToElement(value);
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0])){Console.Error.WriteLine("指定全新的检查目录。");return 2;}
        var folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);
        try
        {
            using var image=new HImage();image.GenImageConst("byte",600,400);
            using var circle=new HRegion();circle.GenCircle(150d,130d,25d);
            using var plate=new HRegion(90d,350d,320d,460d);
            using var marked=image.PaintRegion(circle,220d,"fill");using var fixture=marked.PaintRegion(plate,220d,"fill");
            var path=Path.Combine(folder,"input.png");fixture.WriteImage("png",0,path);
            using var vm=new Scope();var registry=vm.Registry;var validator=new WorkflowGraphValidator(registry);var engine=new WorkflowExecutionEngine(registry,validator);
            NodeInstance Node(string type,string name)=>new(){TypeId=type,DisplayName=name,Parameters=registry.Definitions.Single(d=>d.TypeId==type).Parameters.Where(p=>p.DefaultValue is not null).ToDictionary(p=>p.Id,p=>p.DefaultValue!.Value)};
            var read=Node(HalconNodeProvider.ImageRead,"图像");read=read with{Parameters=new(read.Parameters){["path"]=J(path)}};
            var threshold=Node(HalconNodeProvider.Threshold,"螺丝二值化") with{X=320,Y=80};var blob=Node(HalconNodeProvider.BlobAnalysis,"螺丝计数") with{X=540,Y=80};
            var line=Node(HalconNodeProvider.StraightLineFind,"边缘直线") with{X=320,Y=280};
            line=line with{Parameters=new(line.Parameters){["orientation"]=J("vertical")}};
            var a=new InspectionRegion(Guid.NewGuid(),"P1 · 螺丝",read.Id,"image",new(100,80,200,180));
            var b=new InspectionRegion(Guid.NewGuid(),"P2 · 边缘",read.Id,"image",new(110,330,300,370));
            var ta=new InspectionTask(Guid.NewGuid(),"螺丝数量",a.Id,[threshold.Id,blob.Id],[new(Guid.NewGuid(),blob.Id,"count",InspectionRuleKind.NumberRange,Minimum:1,Maximum:1)]);
            var tb=new InspectionTask(Guid.NewGuid(),"直线存在",b.Id,[line.Id],[new(Guid.NewGuid(),line.Id,"status",InspectionRuleKind.TextEquals,"/Code",Expected:"FOUND")]);
            var flow=new FlowDocument{Name="独立区域任务检查",Nodes=[read,threshold,blob,line],InspectionRegions=[a,b],InspectionTasks=[ta,tb],
                Edges=[new(Guid.NewGuid(),read.Id,"image",threshold.Id,"image"),new(Guid.NewGuid(),threshold.Id,"region",blob.Id,"region"),new(Guid.NewGuid(),read.Id,"image",line.Id,"image")],
                DetectionPanels=[new(Guid.NewGuid(),read.Id,Contents:[new(Guid.NewGuid(),read.Id,Overlays:[new(blob.Id),new(line.Id)])])]};
            using(var run=engine.RunAsync(flow).GetAwaiter().GetResult())
            {
                Check(run.Status==WorkflowRunStatus.Success,string.Join(";",run.Errors));
                Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"真实Blob与直线任务共同OK");
                Check(run.NodeResults.Single(r=>r.NodeId==blob.Id).Outputs["count"].GetInt32()==1,"Blob只统计所属ROI");
                Check(!threshold.Parameters.ContainsKey("__taskRoi")&&threshold.Parameters["roi"].GetString()=="","任务执行不改写节点原参数");
                var sourceFrame=run.SpatialOutputs![read.Id]["image"];
                foreach(var wrong in new[]{sourceFrame with{FrameId=Guid.NewGuid()},sourceFrame with{RunId=Guid.NewGuid()},sourceFrame with{SpaceId=Guid.NewGuid()},sourceFrame with{Annotated=true},sourceFrame with{Count=2}})
                {
                    var rejected=false;try{InspectionPlan.RequireFrame(flow,threshold.Id,new Dictionary<string,FrameReference>{{"image",wrong}},run.SpatialOutputs,run.Id);}catch(InvalidDataException){rejected=true;}
                    Check(rejected,"任务拒绝错帧/跨空间/标注/多图来源");
                }
                File.WriteAllText(Path.Combine(folder,"run.json"),JsonSerializer.Serialize(run.Inspection,new JsonSerializerOptions{WriteIndented=true}));
            }
            var store=new ProjectStore();var workspace=store.CreateProject(Path.Combine(folder,"独立区域任务.hvproj"),"独立区域任务");store.SaveProject(workspace,[flow]);
            var restored=store.OpenProject(workspace.ProjectFilePath).Project.Flows.Single();
            using(var run=engine.RunAsync(restored).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"保存重开真实任务仍OK");
            using(var run=engine.RunAsync(store.CopyFlow(workspace,flow,"复制")).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Ok,"复制方案真实任务仍OK");
            var ng=flow with{InspectionTasks=[ta with{Rules=[ta.Rules[0] with{Minimum=2,Maximum=2}]},tb]};
            using(var run=engine.RunAsync(ng).GetAwaiter().GetResult())Check(run.Status==WorkflowRunStatus.Success&&run.Inspection?.Verdict==InspectionVerdict.Ng,"成功执行与产品NG分离");
            var outside=flow with{InspectionRegions=[a with{Bounds=new(0,0,500,700)},b]};
            using(var run=engine.RunAsync(outside).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Error,"越界区域ERROR");
            var noRules=flow with{InspectionTasks=[ta with{Rules=[]},tb]};
            using(var run=engine.RunAsync(noRules).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Error,"空规则ERROR");
            var badField=flow with{InspectionTasks=[ta with{Rules=[ta.Rules[0] with{Pointer="/不存在"}]},tb]};
            using(var run=engine.RunAsync(badField).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Error,"未知字段ERROR");
            var duplicate=flow with{InspectionTasks=[ta,tb with{NodeIds=[threshold.Id,line.Id]}]};
            Check(validator.Validate(duplicate).Any(i=>i.Message.Contains("多个任务")),"多任务共用可变节点被阻止");
            var extra=Node(HalconNodeProvider.Threshold,"面积复核");
            var shared=flow with{Nodes=[..flow.Nodes,extra],Edges=[..flow.Edges,new(Guid.NewGuid(),read.Id,"image",extra.Id,"image")],
                InspectionTasks=[..flow.InspectionTasks,new(Guid.NewGuid(),"同区域面积复核",a.Id,[extra.Id],[new(Guid.NewGuid(),extra.Id,"area",InspectionRuleKind.NumberRange,Minimum:100,Maximum:5000)])]};
            using(var run=engine.RunAsync(shared).GetAwaiter().GetResult())Check(run.Inspection?.Verdict==InspectionVerdict.Ok&&run.Inspection.Tasks.Count==3,"同一区域可关联不同算法任务");
            var missingTerminal=flow with{InspectionTasks=[ta with{NodeIds=[threshold.Id,blob.Id,line.Id]},tb with{Enabled=false,Required=false,NodeIds=[]}]};
            Check(validator.Validate(missingTerminal).Any(i=>i.Message.Contains("末端算法")),"独立算法分支缺少末端规则被阻止");
            for(var i=0;i<100;i++)
            {
                var run=engine.RunAsync(i%2==0?flow:ng).GetAwaiter().GetResult();
                var objects=run.RuntimeOutputs!.Values.SelectMany(v=>v.Values).OfType<HObject>().ToArray();
                Check(run.Inspection?.Verdict==(i%2==0?InspectionVerdict.Ok:InspectionVerdict.Ng),"交替任务判定无旧结果");run.Dispose();Check(objects.All(o=>!o.IsInitialized()),"任务输出对象释放");
            }
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();using var run=engine.RunAsync(flow,cancellationToken:cancelled.Token).GetAwaiter().GetResult();Check(run.Inspection?.Verdict==InspectionVerdict.Error,"任务取消不放行");}
            CheckEditor(workspace.ProjectFilePath,folder);
            File.WriteAllText(Path.Combine(folder,"summary.json"),JsonSerializer.Serialize(new{checks=Passed.Count,Passed},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {Passed.Count} 项真实HALCON任务检查");return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(folder,"error.txt"),error.ToString());File.WriteAllText(Path.Combine(folder,"passed-before-error.json"),JsonSerializer.Serialize(Passed));Console.Error.WriteLine(error);return 1;}
    }
    private static void CheckEditor(string project,string folder)
    {
        var app=new App();app.InitializeComponent();var window=new MainWindow(project);window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var workspace=(WorkflowWorkspaceViewModel)window.DataContext;InspectionTaskEditorWindow? editor=null;
        try
        {
            var flow=workspace.SelectedFlow!;editor=new(window,flow,workspace.NodeCatalog,null);editor.Show();editor.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            var root=(FrameworkElement)editor.Content;
            var members=(ListBox)editor.FindName("MemberList");
            Check(members.SelectedItems.Count==2,"任务编辑器恢复算法分支选择");
            Check(editor.Regions.Count==2&&editor.Tasks.Count==2,"任务编辑器恢复区域及任务");
            Check(Descendants(root).OfType<TextBlock>().Any(t=>t.Text=="图像 · Image"),"来源下拉框显示可读名称");
            Check(!Descendants(root).OfType<TextBlock>().Any(t=>t.Text.StartsWith("InspectionSourceChoice")),"下拉框不泄露内部记录结构");
            foreach(var button in Descendants(root).OfType<Button>().Where(b=>b.Content as string is "添加检测区域" or "移除检测区域" or "绘制任务区域"))
                Check(Descendants(button).OfType<TextBlock>().Any(t=>t.FontFamily.Source=="Segoe MDL2 Assets"),"任务区域操作显示图标");
            foreach(var width in new[]{920d,1100d,1300d})
            {
                root.Measure(new Size(width,640));root.Arrange(new Rect(0,0,width,640));root.UpdateLayout();
                foreach(var button in Descendants(root).OfType<Button>().Where(b=>b.IsVisible&&b.ActualWidth>0))
                {
                    var parent=VisualTreeHelper.GetParent(button) as FrameworkElement;
                    if(parent is null)continue;
                    var point=button.TranslatePoint(new Point(),parent);
                    Check(point.X>=-.5&&point.X+button.ActualWidth<=parent.ActualWidth+.5,"任务窗口按钮横向边界 "+width);
                    Check(ToolTipService.GetInitialShowDelay(button)==1500&&ToolTipService.GetBetweenShowDelay(button)==0,"任务操作提示1500ms且切换不立即弹出");
                }
            }
            var apply=Descendants(root).OfType<Button>().Single(b=>b.Content as string=="应用任务配置");
            ((TabControl)Descendants(root).OfType<TabControl>().Single()).SelectedIndex=1;root.UpdateLayout();
            Check(((DataGrid)editor.FindName("RulesGrid")).RowBackground is SolidColorBrush rowColor&&rowColor.Color==Color.FromRgb(32,46,57),"规则行使用深色背景，避免浅色文字叠白底");
            Check(Descendants(root).OfType<CheckBox>().All(c=>c.Foreground is SolidColorBrush color&&color.Color==Color.FromRgb(220,229,233)),"任务复选框文字对比可读");
            var taskList=(ListBox)editor.FindName("TaskList");taskList.SelectedIndex=1;
            Descendants(root).OfType<Button>().Single(b=>b.Content as string=="添加判定规则").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var lineRule=editor.Tasks[1].Rules.Last();
            Check(lineRule.Kind==InspectionRuleKind.TextEquals&&lineRule.PortId=="status"&&lineRule.Pointer=="/Code"&&lineRule.Expected=="FOUND","直线默认规则检查FOUND，不误用匹配状态");
            editor.Tasks[1].Rules.Remove(lineRule);taskList.SelectedIndex=0;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(flow.Document.InspectionTasks.Single(t=>t.Name=="螺丝数量").NodeIds.Count==2,"实际应用按钮保持所选算法分支");
            editor.Tasks[0].Rules[0].Minimum="2";editor.Tasks[0].Rules[0].Maximum="2";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(flow.Document.InspectionTasks[0].Rules[0].Minimum==2,"实际应用按钮写入判定阈值");
            editor.Tasks[0].Enabled=false;apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(flow.Document.InspectionTasks[0].Enabled&&((TextBlock)editor.FindName("Feedback")).Text.Contains("不能禁用"),"无效必检配置被界面阻止");
            editor.Tasks[0].Enabled=true;
            var run=Complete(workspace.RunSelectedFlowAsync());workspace.SetExecutionRun(run);
            Check(run.Inspection?.Verdict==InspectionVerdict.Ng&&workspace.ProductVerdictText.Contains("NG"),"工作区展示真实产品NG");
            var display=flow.Document.DetectionPanels.Single().Contents!.Single();
            var scene=DetectionOverlayScene.Create(flow,run,display,600,400);
            Check(scene.Captions.Any(c=>c.Text.Contains("螺丝数量 NG")&&c.Color=="Red"),"检测叠加显示任务NG");
            var missingTasks=run with{Inspection=run.Inspection! with{Tasks=[],Verdict=InspectionVerdict.Error}};
            scene=DetectionOverlayScene.Create(flow,missingTasks,display,600,400);
            Check(scene.Captions.Where(c=>c.Text.StartsWith("P1")||c.Text.StartsWith("P2")).All(c=>c.Color=="Orange"&&c.Text.Contains("ERROR")),"缺少任务结果显示ERROR，不能标绿");
            flow.SetParameter(flow.Document.Nodes.Single(n=>n.TypeId==HalconNodeProvider.Threshold).Id,"minimum",J(130));
            Check(workspace.ProductVerdictText.Contains("配置已更改"),"修改算法配置不继续展示旧产品结论");
            workspace.SaveAll();var saved=new ProjectStore().OpenProject(project).Project;
            Check(saved.Flows.Single().InspectionTasks[0].Rules[0].Minimum==2,"实际工作区保存恢复任务阈值");
            var old=run.RuntimeOutputs!.Values.SelectMany(v=>v.Values).OfType<HObject>().ToArray();
            workspace.OpenProject(project);Check(workspace.LastRun is null&&old.All(o=>!o.IsInitialized()),"任务项目切换释放旧图像");
        }
        finally{editor?.Close();Complete(workspace.ShutdownAsync());window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);SynchronizationContext.SetSynchronizationContext(oldContext);}
    }
    private static void Complete(Task task){var frame=new DispatcherFrame();var dispatcher=System.Windows.Application.Current.Dispatcher;_ = task.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);if(!task.IsCompleted)Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
    private static T Complete<T>(Task<T> task){Complete((Task)task);return task.GetAwaiter().GetResult();}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var item in Descendants(child))yield return item;}}
    private sealed class Scope:IDisposable
    {
        private readonly HalconCameraManager _camera=new();private readonly DlModelSessionCache _dl=new();private readonly ShapeMatchModelCache _models=new();
        public NodeRegistry Registry{get;}
        public Scope(){Registry=BuiltInNodeCatalog.Create();Registry.Register(new HalconNodeProvider(),[new HalconImageReadExecutor(),new HalconCameraCaptureExecutor(_camera),new HalconThresholdExecutor(),new HalconBlobAnalysisExecutor(),new HalconStraightLineFindExecutor(),new HalconClassificationExecutor(_dl),new HalconSemanticSegmentationExecutor(_dl),new HalconObjectDetectionExecutor(_dl),new HalconQuickMatchExecutor(_models),new HalconPositionCorrectionExecutor()]);}
        public void Dispose(){_models.Dispose();_dl.Dispose();_camera.Dispose();}
    }
}
