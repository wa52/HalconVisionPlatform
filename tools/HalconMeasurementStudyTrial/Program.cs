using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HalconVision.App;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Persistence;

internal static class Program
{
    private static readonly List<string> Checks = [];
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Checks.Add(name); }
    private static void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true,name); return; } catch (InvalidOperationException) { Check(true,name); return; } throw new Exception("未拒绝："+name); }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static readonly NodeInstance Node = new() { TypeId="fixture.length", DisplayName="长度", Parameters=new(){{"s",J(1)}} };
    private static readonly FlowDocument Flow = new() { Nodes=[Node] };
    private static MeasurementStudy New(double reference=10) => new(new(Flow.Id,Node.Id,"长度","value","长度","mm",InspectionPlan.ConfigurationIdentity(Flow),"标准件",reference));
    private static WorkflowExecutionRun Run(double? value, NodeExecutionStatus status=NodeExecutionStatus.Success)
    {
        var id=Guid.NewGuid(); var now=DateTime.UtcNow;
        return new(id,Flow.Id,now,now,WorkflowRunStatus.Success,[new(Node.Id,"长度",status,now,now,1,new Dictionary<string,JsonElement>{{"value",J(value)}},null)],[],[],
            SpatialOutputs:new Dictionary<Guid,IReadOnlyDictionary<string,FrameReference>>{{Node.Id,new Dictionary<string,FrameReference>{{"value",new(id,Guid.NewGuid(),Node.Id,"image",Guid.NewGuid(),512,384,1,false,false,WorkflowConfiguration.NodeSignature(Flow,Node.Id))}}}},
            Inspection:new(id,Flow.Id,InspectionVerdict.NotConfigured,[],[],InspectionPlan.ConfigurationIdentity(Flow)));
    }
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=2 || Directory.Exists(args[0])) return 2;
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        try { Core(root); Ui(root,Path.GetFullPath(args[1])); File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(new{count=Checks.Count,Checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {Checks.Count} 项测量验证检查");return 0; }
        catch(Exception ex){File.WriteAllText(Path.Combine(root,"error.txt"),ex.ToString());Console.Error.WriteLine(ex);return 1;}
    }
    private static void Core(string root)
    {
        var study=New();Check(study.Statistics.Mean is null && study.Statistics.SampleStandardDeviation is null,"空记录不伪造零值");
        foreach(var number in new[]{9d,10d,11d}) {using var run=Run(number);study.Capture(Flow,run,"中心");Reject(()=>study.Capture(Flow,run,"重复"),"同一运行不能重复录入");}
        var s=study.Statistics;Check(s.Mean==10 && s.Bias==0 && s.SampleStandardDeviation==1 && s.Range==2 && s.MaximumAbsoluteError==1,"已知序列均值偏差样本标准差及最大误差");
        foreach(var status in new[]{NodeExecutionStatus.Success,NodeExecutionStatus.Failed,NodeExecutionStatus.Skipped}){using var missing=Run(null,status);study.Capture(Flow,missing,"失测");}
        Check(study.Statistics is {Total:6,Valid:3,Missing:3,Mean:10,SampleStandardDeviation:1},"失测保留计数但不混入统计");
        var one=New();using(var run=Run(0))one.Capture(Flow,run,"零值");Check(one.Statistics.Mean==0 && one.Statistics.SampleStandardDeviation is null,"真实零值有效且单样本无标准差");
        foreach(var state in new[]{WorkflowRunStatus.Failed,WorkflowRunStatus.Cancelled}){var failed=New();using var run=Run(10);failed.Capture(Flow,run with{Status=state},"流程异常");Check(failed.Statistics is{Missing:1,Valid:0},"流程未成功即使有数值也为失测 "+state);}
        var negative=New(-10);using(var run=Run(-12))negative.Capture(Flow,run,"负净距");Check(negative.Statistics.Bias==-2,"有符号净距保留误差符号");
        foreach(var reference in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})Reject(()=>New(reference),"非有限参考值拒绝");
        using(var run=Run(10))
        {
            Reject(()=>study.Capture(Flow,run," "),"空样本标识拒绝");
            Reject(()=>study.Capture(Flow with{Id=Guid.NewGuid()},run,"跨流程"),"跨流程拒绝");
            Reject(()=>study.Capture(Flow with{Nodes=[Node with{Parameters=new(){{"s",J(2)}}}]},run,"改参数"),"配置改变拒绝混入");
            Reject(()=>study.Capture(Flow,run with{Inspection=run.Inspection! with{ConfigurationIdentity="旧"}},"过期"),"旧结果拒绝");
            var frame=run.SpatialOutputs![Node.Id]["value"];
            foreach(var wrong in new[]{frame with{RunId=Guid.NewGuid()},frame with{Annotated=true},frame with{Count=2},frame with{ConfigurationIdentity="旧"}})
            {var failed=New();var stale=run with{SpatialOutputs=new Dictionary<Guid,IReadOnlyDictionary<string,FrameReference>>{{Node.Id,new Dictionary<string,FrameReference>{{"value",wrong}}}}};failed.Capture(Flow,stale,"错帧");Check(failed.Statistics is{Valid:0,Missing:1},"无效帧来源不成为数值");}
            var resized=run with{SpatialOutputs=new Dictionary<Guid,IReadOnlyDictionary<string,FrameReference>>{{Node.Id,new Dictionary<string,FrameReference>{{"value",frame with{Width=640}}}}}};
            Reject(()=>study.Capture(Flow,resized,"尺寸改变"),"不同成像尺寸拒绝混入");
        }
        var overflow=New(0);using(var first=Run(1e200))overflow.Capture(Flow,first,"极端1");using(var second=Run(-1e200))Reject(()=>overflow.Capture(Flow,second,"极端2"),"统计溢出不提交");Check(overflow.Samples.Count==1,"拒绝后旧记录完整");
        var baseline=New(1e12);foreach(var v in new[]{1e12-.25,1e12,1e12+.25}){using var run=Run(v);baseline.Capture(Flow,run,"大基线");}Check(baseline.Statistics.SampleStandardDeviation==.25,"大基线小波动不丢精度");
        var quoted=New();using(var run=Run(12))quoted.Capture(Flow,run,"=SUM(1,2)\n含\"引号");var csv=quoted.ToCsv();Check(csv.Contains("'=SUM(1,2)")&&csv.Contains("\"\"引号"),"CSV转义引号换行并阻止文本公式");
        var culture=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(quoted.ToCsv().Contains(",12,2,"),"CSV数值固定小数格式");}finally{CultureInfo.CurrentCulture=culture;}
        var report=JsonSerializer.Deserialize<MeasurementStudyReport>(study.ToJson())!;Check(report.Samples.Count==6&&report.Statistics==study.Statistics&&report.Configuration.GetProperty("Nodes").GetArrayLength()==1,"JSON恢复统计与冻结配置");
        File.WriteAllText(Path.Combine(root,"known-report.json"),study.ToJson());File.WriteAllText(Path.Combine(root,"known-report.csv"),study.ToCsv());
        var definitions=new[]{new NodeDefinition("fixture.length",1,"测量","测量","",[new("value","长度",PortDirection.Output,"number",Unit:"mm"),new("angle","角度",PortDirection.Output,"number",Unit:"rad"),new("unknown","无单位",PortDirection.Output,"number")],[])};
        Check(MeasurementStudy.AvailableFields(Flow,definitions).Count==1,"只提供声明单位的长度输出");
    }
    private static void Pump(Task task){while(!task.IsCompleted)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);task.GetAwaiter().GetResult();}
    private static void Ui(string root,string input)
    {
        var store=new ProjectStore();var source=store.OpenProject(input);var document=source.Project.Flows.Single();
        // A new project referencing existing synthetic inputs; never edits the source fixture.
        var originalReader=document.Nodes.First(n=>n.TypeId=="halcon.image-read");
        var originalPath=originalReader.Parameters["path"].GetString()!;
        var image=Directory.Exists(originalPath)?Directory.GetFiles(originalPath,"*.png").First(p=>Path.GetFileName(p).Contains("line")):originalPath;
        document=document with{Nodes=document.Nodes.Select(n=>n.Id==originalReader.Id?n with{Parameters=new(n.Parameters){["path"]=J(image)}}:n).ToList()};
        var path=Path.Combine(root,"合成标准件验证.hvproj");var target=store.CreateProject(path,"合成标准件验证");store.SaveProject(target,[document]);
        var app=new App();app.InitializeComponent();var main=new MainWindow(path);main.Show();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        var vm=(WorkflowWorkspaceViewModel)main.DataContext;var window=new MeasurementStudyWindow(main,vm);window.Show();
        try
        {
            var field=(ComboBox)window.FindName("FieldSelector");field.SelectedItem=field.Items.Cast<MeasurementStudyWindow.FieldChoice>().Single(f=>f.PortId=="lengthMm");
            ((TextBox)window.FindName("ReferenceName")).Text="合成演算标准件";((TextBox)window.FindName("ReferenceValue")).Text="15";((TextBox)window.FindName("SampleName")).Text="中心 · 同图演算";
            var capture=(Button)window.FindName("CaptureButton");var feedback=(TextBlock)window.FindName("Feedback");
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(feedback.Text.Contains("先在主窗口"),"未运行提供下一步反馈");
            for(var i=0;i<3;i++)
            {
                var task=vm.RunSelectedFlowAsync();Pump(task);vm.SetExecutionRun(task.Result);
                Check(task.Result.Status==WorkflowRunStatus.Success,"实际HALCON找线及毫米换算 "+i);
                capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            var rows=(DataGrid)window.FindName("SamplesGrid");Check(rows.Items.Count==3,"实际按钮记录三轮");
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(rows.Items.Count==3 && feedback.Text.Contains("已经记录"),"实际按钮拒绝重复轮次");
            Check(!((Grid)window.FindName("SettingsPanel")).IsEnabled,"首次记录后锁定字段参考条件");
            var study=(MeasurementStudy)typeof(MeasurementStudyWindow).GetField("_study",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            Check(study.Statistics.Valid==3&&Math.Abs(study.Statistics.Mean!.Value-15)<.01,"实际测量统计约15mm");
            Check(study.Samples.All(s=>s.ImageSource.EndsWith(".png")),"报告保留原图文件来源，识别同图重算");
            File.WriteAllText(Path.Combine(root,"actual-report.json"),study.ToJson());File.WriteAllText(Path.Combine(root,"actual-report.csv"),study.ToCsv());
            var blankPath=Path.Combine(Path.GetDirectoryName(input)!,"blank.png");
            var blank=document with{Nodes=document.Nodes.Select(n=>n.Id==originalReader.Id?n with{Parameters=new(n.Parameters){["path"]=J(blankPath)}}:n).ToList()};
            var missingTask=vm.RunSelectedFlowAsync(flowOverride:blank);Pump(missingTask);vm.SetExecutionRun(missingTask.Result);
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(study.Statistics is{Total:4,Valid:3,Missing:1},"实际空图失测保留三轮统计并单独计数");
            Check(((TextBlock)window.FindName("Feedback")).Text.Contains("失测"),"实际失测显示原因");
            File.WriteAllText(Path.Combine(root,"actual-with-missing.json"),study.ToJson());
            var save=typeof(MeasurementStudyWindow).GetMethod("SaveReport",BindingFlags.Instance|BindingFlags.NonPublic)!;
            feedback.Foreground=System.Windows.Media.Brushes.Orange;
            save.Invoke(window,[Path.Combine(root,"ui-export.json")]);
            Check(JsonSerializer.Deserialize<MeasurementStudyReport>(File.ReadAllText(Path.Combine(root,"ui-export.json")))!.Statistics==study.Statistics,"真实导出方法写入并恢复完整报告");
            Check(feedback.Foreground==window.FindResource("WorkbenchTextBrush"),"错误后成功导出恢复正常提示色");
            save.Invoke(window,[Path.Combine(root,"ui-export.csv")]);Check(File.ReadAllBytes(Path.Combine(root,"ui-export.csv")).Take(3).SequenceEqual(new byte[]{239,187,191}),"CSV以UTF8 BOM保存中文");
            var failed=false;try{save.Invoke(window,[Path.Combine(root,"missing-folder","failure.json")]);}catch(TargetInvocationException){failed=true;}
            Check(failed&&study.Samples.Count==4&&!Directory.GetFiles(root,"*.tmp").Any(),"导出失败保留内存记录并清理临时文件");
            var wrong=false;try{save.Invoke(window,[Path.Combine(root,"do-not-create.png")]);}catch(TargetInvocationException){wrong=true;}Check(wrong&&!File.Exists(Path.Combine(root,"do-not-create.png")),"导出拒绝覆盖非报告扩展名");
            var count=study.Samples.Count;vm.LastRun!.Dispose();Check(study.Samples.Count==count&&study.ToJson().Contains("15"),"释放原生图像后统计仍独立可用");
            foreach(var size in new[]{new Size(780,600),new Size(1000,720),new Size(1180,800)})
            {
                window.Width=size.Width;window.Height=size.Height;window.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                foreach(var name in new[]{"SettingsPanel","SamplesGrid","CaptureButton","ExportButton","Feedback"})
                {var e=(FrameworkElement)window.FindName(name);var p=e.TranslatePoint(new Point(),(UIElement)window.Content);Check(p.X>=-1&&p.Y>=-1&&p.X+e.ActualWidth<=((FrameworkElement)window.Content).ActualWidth+1&&p.Y+e.ActualHeight<=((FrameworkElement)window.Content).ActualHeight+1,"边界 "+name+" "+size.Width);}
            }
            window.Width=780;window.Height=600;feedback.Text=string.Concat(Enumerable.Repeat("长路径与错误信息测试/",100));window.UpdateLayout();Check(((FrameworkElement)window.FindName("SamplesGrid")).ActualHeight>80,"长错误不挤走样本表");
            Check(new ActionHelpConverter().Convert("记录当前测量",typeof(string),null!,CultureInfo.InvariantCulture).ToString()!.Contains("同一轮"),"中文图标帮助");
            // Mark the test's explicit file export, then close without discarding unexported data.
            typeof(MeasurementStudyWindow).GetField("_exported",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);
        }
        finally {typeof(MeasurementStudyWindow).GetField("_exported",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);window.Close();main.Close();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);app.Shutdown();}
    }
}
