using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using HalconVision.App;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0])){Console.Error.WriteLine("请指定全新的检查目录。");return 2;}
        Directory.CreateDirectory(args[0]);
        var app=new App();app.InitializeComponent();
        var choices=0;string? nextPath=null;
        var window=new MainWindow(chooseNewProjectPath:_=>{choices++;return nextPath;});
        window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        var vm=(WorkflowWorkspaceViewModel)window.DataContext;
        var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        try
        {
            var file=Path.GetFullPath(Path.Combine(args[0],"empty-project.hvproj"));
            var catalog=(ListBox)window.FindName("NodeCatalogList");
            catalog.SelectedItem=vm.NodeCatalog.Single(n=>n.TypeId==HalconNodeProvider.QuickMatch);
            var root=(FrameworkElement)window.Content;
            root.Measure(new Size(1180,680));root.Arrange(new Rect(0,0,1180,680));root.UpdateLayout();
            var add=Descendants(root).OfType<Button>().Single(b=>b.Content as string=="添加所选节点");
            Check(!vm.HasProject&&vm.Flows.Count==0,"首次启动没有项目和流程");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(!vm.HasProject&&choices==1&&vm.ProjectStatus.StartsWith("已取消添加节点")&&!Directory.GetFiles(args[0],"*.hvproj").Any(),"取消首次项目保存不产生文件或节点，反馈明确");
            nextPath=file;
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(vm.HasProject&&choices==2&&vm.SelectedFlow?.Nodes.Count==1,"首次添加先选择项目路径，再自动加入主流程");
            Check(vm.SelectedFlow!.SelectedNode?.TypeId==HalconNodeProvider.QuickMatch,"新增节点自动选中并显示属性");
            var originalFlow=vm.SelectedFlow;
            vm.SelectedFlow=null;
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(vm.SelectedFlow==originalFlow&&vm.Flows.Count==1&&originalFlow.Nodes.Count==2&&choices==2,"当前流程未选中时复用已有流程，不重建项目或丢失原节点");
            var before=originalFlow.Nodes.Count;
            catalog.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent});
            Check(originalFlow.Nodes.Count==before,"双击节点库空白区域不重复添加旧选中项");
            catalog.SelectedItem=vm.NodeCatalog.Single(n=>n.TypeId==HalconNodeProvider.Threshold);
            root.UpdateLayout();
            var quickText=Descendants(catalog).OfType<TextBlock>().First(t=>t.DataContext is HalconVision.Application.NodeDefinition d&&d.TypeId==HalconNodeProvider.QuickMatch);
            catalog.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=quickText});
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(originalFlow.Nodes.Count==before+1&&originalFlow.SelectedNode?.TypeId==HalconNodeProvider.QuickMatch,"双击以实际命中节点为准且只添加一次");
            foreach(var definition in vm.NodeCatalog)
            {
                before=originalFlow.Nodes.Count;catalog.SelectedItem=definition;add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                Check(originalFlow.Nodes.Count==before+1&&originalFlow.SelectedNode?.TypeId==definition.TypeId,"现有项目实际添加："+definition.DisplayName);
            }
            before=originalFlow.Nodes.Count;originalFlow.Undo();Check(originalFlow.Nodes.Count==before-1,"添加节点可撤销");originalFlow.Redo();Check(originalFlow.Nodes.Count==before,"添加节点可重做");
            catalog.SelectedItem=null;add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(originalFlow.Nodes.Count==before&&vm.ProjectStatus.Contains("选择要添加的节点"),"没有选中项时提示下一步且不添加错误节点");
            vm.SaveAll();
            Check(new ProjectStore().OpenProject(file).Project.Flows.Single().Nodes.Count==before,"保存重开保留自动建立的流程及全部节点");
            File.WriteAllText(Path.Combine(args[0],"checks.txt"),string.Join(Environment.NewLine,checks));
            Console.WriteLine($"PASS {checks.Count} 项实际添加节点检查");return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(args[0],"error.txt"),error.ToString());Console.Error.WriteLine(error.Message);return 1;}
        finally{if(vm.HasProject)vm.SaveAll();window.Close();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var item in Descendants(child))yield return item;}
    }
}
