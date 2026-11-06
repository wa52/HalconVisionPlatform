using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HalconVision.App;
using HalconVision.Domain;
using HalconVision.Persistence;

internal static class Program
{
    private static readonly List<string> Checks=[];
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Checks.Add(label);}
    private static void Pump(Task task){while(!task.IsCompleted)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);task.GetAwaiter().GetResult();}
    [STAThread]private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0]))return 2;var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        var app=new App();app.InitializeComponent();var main=new MainWindow();main.Show();var vm=(WorkflowWorkspaceViewModel)main.DataContext;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var path=Path.Combine(root,"归档检查.hvproj");vm.CreateProject(path,"归档检查");
            using(var image=new HalconDotNet.HImage()){image.GenImageConst("byte",32,24);image.WriteImage("png",0,Path.Combine(root,"input.png"));}
            var source=vm.SelectedFlow!.Document;
            var reader=new NodeInstance{TypeId="halcon.image-read",DisplayName="归档原图",Parameters=new(){{"path",JsonSerializer.SerializeToElement(Path.Combine(root,"input.png"))}}};
            new ProjectStore().SaveProject(vm.Workspace!,[source with{Nodes=[reader]}]);vm.OpenProject(path);
            var folder=Path.Combine(root,"runs");vm.ConfigureRunArchive(new(true,folder));
            Check(new ProjectStore().OpenProject(path).Project.RunArchive==new RunArchiveSettings(true,folder),"设置随项目保存恢复");
            for(var i=0;i<3;i++){var task=vm.RunSelectedFlowAsync();Pump(task);vm.SetExecutionRun(task.Result);Check(!vm.ArchiveFailed&&Directory.GetFiles(folder,"*.run.json").Length==i+1,"完整轮自动归档 "+i);}
            var first=WorkflowArchiveHistory.ReadPage(folder,0);Check(first.Entries.Count==3&&first.Entries.All(e=>e.Run is not null),"历史读取真实运行归档");
            Check(first.Entries.All(e=>e.Run!.NodeResults[0].Outputs.ContainsKey("image")),"实际HALCON读图结果归档不持有图像句柄");
            var partial=vm.RunSelectedFlowAsync(flowOverride:vm.SelectedFlow!.Document with{Nodes=[]});Pump(partial);vm.SetExecutionRun(partial.Result);Check(Directory.GetFiles(folder,"*.run.json").Length==3,"仅上游调试不自动归档");
            Check(first.Entries.All(e=>e.Run!.ProjectId==vm.Workspace!.Project.Id),"项目身份保留");
            File.WriteAllText(Path.Combine(folder,"bad.run.json"),"{");var damaged=WorkflowArchiveHistory.ReadPage(folder,0);Check(damaged.Entries.Count(e=>e.Run is null)==1,"损坏记录不阻止其他记录");
            var seed=damaged.Entries.First(e=>e.Run is not null).File;
            for(var i=0;i<52;i++)File.Copy(seed,Path.Combine(folder,$"copy-{i:D3}.run.json"));
            Check(WorkflowArchiveHistory.ReadPage(folder,0) is{HasNext:true,Entries.Count:50}&&WorkflowArchiveHistory.ReadPage(folder,1) is{HasNext:false,Entries.Count:6},"历史50条分页无漏项");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();var rejected=false;try{WorkflowArchiveHistory.ReadPage(folder,0,cancelled.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected,"取消历史读取");}
            var blocker=Path.Combine(root,"blocked");File.WriteAllText(blocker,"not-a-directory");vm.ConfigureRunArchive(new(true,blocker));var failed=vm.RunSelectedFlowAsync();Pump(failed);vm.SetExecutionRun(failed.Result);
            Check(vm.ArchiveFailed&&vm.ArchiveStatus.Contains("失败"),"归档失败状态明确且不吞检测结果");
            Check(vm.LastRun!.Id==failed.Result.Id,"失败后本轮检测结果保留");
            var sequenceFlow=vm.SelectedFlow!.Document with{Nodes=[reader with{Parameters=new(){{"path",JsonSerializer.SerializeToElement(root)}}}]};
            new ProjectStore().SaveProject(vm.Workspace!,[sequenceFlow]);vm.OpenProject(path);vm.ConfigureRunArchive(new(true,blocker));
            typeof(MainWindow).GetMethod("ContinuousRun_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,[main,new RoutedEventArgs()]);
            var execution=(Task?)typeof(MainWindow).GetField("_executionTask",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main);
            if(execution is not null)Pump(execution);Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(vm.ArchiveFailed&&!vm.IsContinuousRunning&&!vm.IsRunning,"真实连续运行遇归档失败停止并完成收尾");
            vm.ConfigureRunArchive(new(false,folder));var disabled=vm.RunSelectedFlowAsync();Pump(disabled);vm.SetExecutionRun(disabled.Result);Check(!vm.ArchiveFailed&&Directory.GetFiles(folder,"*.run.json").Length==56,"禁用后不新增文件");
            vm.ConfigureRunArchive(new(true,folder));
            var window=new RunArchiveWindow(main,vm);window.Show();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            try
            {
                // Wait for the real async Loaded query to finish through the dispatcher.
                var timeout=DateTime.UtcNow.AddSeconds(15);while(((DataGrid)window.FindName("History")).Items.Count==0&&DateTime.UtcNow<timeout)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);
                var grid=(DataGrid)window.FindName("History");Check(grid.Items.Count==50,"实际历史窗口加载分页");grid.SelectedItem=grid.Items.Cast<ArchiveHistoryEntry>().First(e=>e.Run is not null);
                ((CheckBox)window.FindName("EnabledArchive")).IsChecked=false;((Button)window.FindName("SaveSettings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!new ProjectStore().OpenProject(path).Project.RunArchive.Enabled,"真实保存设置按钮持久化开关");
                Check(((TextBox)window.FindName("ResultDetail")).Text.Contains("RunId")&&((TextBox)window.FindName("FlowDetail")).Text.Contains("快照"),"选择记录显示结果和流程详情");
                ((TextBox)window.FindName("Search")).Text="没有这个项目";Check(grid.Items.Count==0&&((TextBlock)window.FindName("Empty")).Visibility==Visibility.Visible,"本页筛选空状态");
                foreach(var size in new[]{new Size(800,600),new Size(1100,760),new Size(1366,768)})
                {window.Width=size.Width;window.Height=size.Height;window.UpdateLayout();foreach(var name in new[]{"Folder","SaveSettings","History","Feedback"}){var e=(FrameworkElement)window.FindName(name);var p=e.TranslatePoint(new Point(),(UIElement)window.Content);Check(p.X>=0&&p.Y>=0&&p.X+e.ActualWidth<=((FrameworkElement)window.Content).ActualWidth+1,"布局边界 "+size.Width+name);}}
            }finally{window.Close();}
            File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(new{count=Checks.Count,Checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {Checks.Count} 项归档工作台检查");return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"error.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
        finally{main.Close();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
}
