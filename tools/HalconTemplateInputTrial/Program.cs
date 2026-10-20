using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0])) return 2;
        var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var app=new App();app.InitializeComponent();var window=new MainWindow();window.Show();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var vm=(WorkflowWorkspaceViewModel)window.DataContext;
        var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks.Add(label);}
        try
        {
            var input=Path.Combine(output,"input");Directory.CreateDirectory(input);
            using var zero=new HImage();zero.GenImageConst("byte",160,120);
            using var white=zero.ScaleImage(0d,200d);
            zero.WriteImage("png",0,Path.Combine(input,"01.png"));white.WriteImage("png",0,Path.Combine(input,"02.png"));
            var project=Path.Combine(output,"check.hvproj");vm.CreateProject(project,"模板输入检查");var flow=vm.SelectedFlow!;
            flow.AddNode(vm.NodeCatalog.Single(d=>d.TypeId==HalconNodeProvider.ImageRead));var reader=flow.SelectedNode!;var readerId=reader.Id;
            reader.Parameters.Single(p=>p.Definition.Id=="path").ValueText=input;reader.Parameters.Single(p=>p.Definition.Id=="path").Commit();
            flow.AddNode(vm.NodeCatalog.Single(d=>d.TypeId==HalconNodeProvider.QuickMatch));var matchId=flow.SelectedNode!.Id;
            Check(flow.Connect(readerId,"image",matchId,"image",out _),"上游图像端口连接成功");flow.SelectNode(matchId,false);
            var copy=typeof(MainWindow).GetMethod("CopyQuickMatchInput",BindingFlags.NonPublic|BindingFlags.Instance)!;
            try {using var unexpected=(HImage)copy.Invoke(window,[flow,flow.SelectedNode!])!;Check(false,"没有运行结果时不能擅自读取文件夹第一张冒充上游当前帧");}
            catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException){Check(true,"没有当前运行帧时明确拒绝，不读取文件夹第一张");}
            var acquire=vm.RunTemplateUpstreamAsync(matchId);PumpUntil(()=>acquire.IsCompleted);acquire.GetAwaiter().GetResult();
            Check(vm.LastRun!.NodeResults.Count==1&&vm.LastRun.NodeResults.Single().NodeId==readerId,"无模板时能够独立运行上游子图");
            var prepared=vm.PrepareNextImageFlow(out _,out _,out _)!;Run(prepared with {Nodes=prepared.Nodes.Where(n=>n.Id==readerId).ToList(),Edges=[]});
            using(var current=(HImage)copy.Invoke(window,[flow,flow.SelectedNode!])!)Check(current.GetGrayval(0,0).D==200,"模板接收上游第二帧，而非文件夹第一张");
            window.UpdateLayout();var capture=Descendants(window).OfType<Button>().Single(b=>b.Tag is NodeParameterViewModel {IsQuickMatchTemplateRoi:true} && b.Content as string=="取当前图像为模板");
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>flow.SelectedNode!.Parameters.Single(p=>p.IsQuickMatchTemplate).ValueText.Length>0);
            var path=flow.SelectedNode!.Parameters.Single(p=>p.IsQuickMatchTemplate).ValueText;
            Check(File.Exists(path)&&Path.GetFullPath(path).StartsWith(output+Path.DirectorySeparatorChar),"模板自动保存在项目内，无另存对话框");
            using(var saved=new HImage(path))Check(saved.GetGrayval(0,0).D==200,"快照像素来自真实运行期 HImage");
            Check(flow.Document.AssetReferences.Count==1,"模板资源加入项目引用");
            var source=flow.Nodes.Single(n=>n.Id==readerId).Parameters.Single(p=>p.Definition.Id=="path");source.ValueText=Path.Combine(input,"01.png");source.Commit();
            try {using var stale=(HImage)copy.Invoke(window,[flow,flow.SelectedNode!])!;Check(false,"配置变化后不能接受旧帧");}
            catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException){Check(true,"配置变化后拒绝旧运行帧");}
            window.UpdateLayout();capture=Descendants(window).OfType<Button>().Single(b=>b.Tag is NodeParameterViewModel {IsQuickMatchTemplateRoi:true} && b.Content as string=="取当前图像为模板");
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>!vm.IsRunning&&flow.SelectedNode!.Parameters.Single(p=>p.IsQuickMatchTemplate).ValueText!=path);
            var refreshed=flow.SelectedNode!.Parameters.Single(p=>p.IsQuickMatchTemplate).ValueText;
            Check(vm.LastRun!.NodeResults.All(n=>n.NodeId==readerId),"首次或失效帧仅执行真实上游子图，不运行未示教的匹配节点");
            using(var current=new HImage(refreshed))Check(current.GetGrayval(0,0).D==0,"配置变更后自动执行上游并建立新模板");
            using(var frozen=new HImage(path))Check(frozen.GetGrayval(0,0).D==200,"重新示教生成独立快照，不覆盖已有模板");
            Check(flow.Document.AssetReferences.Count==2,"重新示教保留资源引用，支持撤销后的旧模板");
            vm.SaveAll();var restored=new ProjectStore().OpenProject(project).Project.Flows.Single();
            Check(restored.Nodes.Single(n=>n.Id==matchId).Parameters["templateImage"].GetString()==refreshed&&restored.AssetReferences.Count==2,"保存恢复模板路径和资源引用");
            source.ValueText=Path.Combine(output,"missing.png");source.Commit();
            var failed=vm.RunTemplateUpstreamAsync(matchId);PumpUntil(()=>failed.IsCompleted);
            try{failed.GetAwaiter().GetResult();Check(false,"上游读取失败不能继续示教");}catch(InvalidOperationException){Check(vm.LastRun!.NodeResults.All(n=>n.Status!=HalconVision.Domain.NodeExecutionStatus.Success),"上游失败替换旧结果，不保留旧图像供示教");}
            source.ValueText=Path.Combine(input,"01.png");source.Commit();
            Check(!flow.SelectedNode!.PrimaryParameters.Concat(flow.SelectedNode.AdvancedParameters).Any(p=>p.IsQuickMatchTemplate),"界面没有需要手选的模板图像参数");
            var taskField=typeof(MainWindow).GetField("_executionTask",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var finish=typeof(MainWindow).GetMethod("FinishRoiDrawing",BindingFlags.NonPublic|BindingFlags.Instance)!;
            foreach(var action in new[]{"单次运行","连续运行","绘制模板"})
            {
                var template=flow.SelectedNode!.Parameters.Single(p=>p.IsQuickMatchTemplate);template.ValueText="";template.Commit();window.UpdateLayout();
                var button=Descendants(window).OfType<Button>().Single(b=>b.Content as string==action&&(action!="绘制模板"||b.Tag is NodeParameterViewModel {IsQuickMatchTemplateRoi:true}));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>taskField.GetValue(window) is null&&template.ValueText.Length>0);
                Check(File.Exists(template.ValueText)&&vm.ProjectStatus.StartsWith("等待模板示教"),action+"：空模板自动获取上游，不弹必填参数错误");
                Check(vm.LastRun!.NodeResults.All(n=>n.NodeId==readerId),action+"：准备模板不执行自身匹配、不报告检测通过");
                Check(((UIElement)window.FindName("RoiDrawingOverlay")).IsHitTestVisible,action+"：自动进入可交互框选状态");
                Check(!vm.IsRunning&&!vm.IsContinuousRunning,action+"：准备完成后没有后台循环");
                Check(Grid.GetColumnSpan((UIElement)window.FindName("RuntimeImagePanel"))==3&&((UIElement)window.FindName("RuntimeResultsPanel")).Visibility==Visibility.Collapsed,action+"：显示放大的独立模板绘制区");
                finish.Invoke(window,null);
                Check(Grid.GetColumnSpan((UIElement)window.FindName("RuntimeImagePanel"))==1&&((UIElement)window.FindName("RuntimeResultsPanel")).Visibility==Visibility.Visible,action+"：结束绘制恢复原布局");
            }
            var finalRun=Descendants(window).OfType<Button>().Single(b=>b.Content as string=="单次运行");
            finalRun.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>taskField.GetValue(window) is null);
            Check(vm.ProjectStatus.StartsWith("单次运行完成")&&!vm.ProjectStatus.StartsWith("等待模板示教"),"执行匹配后状态栏不残留模板准备提示");
            File.WriteAllText(Path.Combine(output,"checks.txt"),string.Join(Environment.NewLine,checks));Console.WriteLine($"PASS {checks.Count} 项上游模板检查");return 0;
            void Run(HalconVision.Domain.FlowDocument document){var task=vm.RunSelectedFlowAsync(flowOverride:document);PumpUntil(()=>task.IsCompleted);vm.SetExecutionRun(task.GetAwaiter().GetResult());}
            void PumpUntil(Func<bool> done){var end=DateTime.UtcNow.AddSeconds(20);while(!done()){if(DateTime.UtcNow>end)throw new TimeoutException("上游模板操作未完成");window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Thread.Sleep(5);}window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);}
        }
        catch(Exception e){Console.Error.WriteLine(e);File.WriteAllText(Path.Combine(output,"error.txt"),e.ToString());return 1;}
        finally{if(vm.HasProject)vm.SaveAll();window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var item in Descendants(child))yield return item;}}
}
