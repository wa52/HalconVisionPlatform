using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Persistence;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Passed=[];
    private static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);_checks++;Passed.Add(name);}
    private static JsonElement J(object? value)=>JsonSerializer.SerializeToElement(value);
    private static int Main(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0])){Console.Error.WriteLine("指定全新的检查输出目录。");return 2;}
        var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        try
        {
            var node=new NodeInstance{TypeId="fixture",DisplayName="输出"};
            var task=new InspectionTask(Guid.NewGuid(),"必检",Guid.NewGuid(),[node.Id],[new(Guid.NewGuid(),node.Id,"value",InspectionRuleKind.NumberRange,Minimum:1,Maximum:2)]);
            var flow=new FlowDocument{Nodes=[node],InspectionTasks=[task]};
            var id=Guid.NewGuid();
            ProductInspectionResult Run(JsonElement value,InspectionTask? replacement=null,NodeExecutionStatus state=NodeExecutionStatus.Success,WorkflowRunStatus runStatus=WorkflowRunStatus.Success)
                => ProductInspectionEvaluator.Evaluate(flow with{InspectionTasks=[replacement??task]},id,runStatus,[new(node.Id,node.DisplayName,state,DateTime.UtcNow,DateTime.UtcNow,0,new Dictionary<string,JsonElement>{{"value",value}},null)],[]);
            foreach(var n in new[]{1d,1.5,2})Check(Run(J(n)).Verdict==InspectionVerdict.Ok,"闭区间合格 "+n);
            foreach(var n in new[]{-1d,0,2.01,100})Check(Run(J(n)).Verdict==InspectionVerdict.Ng,"正常数值不合格 "+n);
            foreach(var value in new[]{J(null),J("1"),J(true),J(new{}),J(new[]{1})})Check(Run(value).Verdict==InspectionVerdict.Error,"错误类型/空值不能放行");
            using(var infinite=JsonDocument.Parse("1e999"))Check(Run(infinite.RootElement).Verdict==InspectionVerdict.Error,"非有限结果不能放行");
            foreach(var state in new[]{NodeExecutionStatus.Failed,NodeExecutionStatus.Skipped,NodeExecutionStatus.Running,NodeExecutionStatus.Idle})Check(Run(J(1),state:state).Verdict==InspectionVerdict.Error,"未完成不能放行 "+state);
            Check(Run(J(1),runStatus:WorkflowRunStatus.Cancelled).Verdict==InspectionVerdict.Error,"取消不能放行");
            Check(Run(J(1),task with{Enabled=false}).Verdict==InspectionVerdict.Error,"必检禁用不能放行");
            Check(Run(J(1),task with{Rules=[]}).Verdict==InspectionVerdict.Error,"无规则不能放行");
            Check(Run(J(1),task with{NodeIds=[]}).Verdict==InspectionVerdict.Error,"无算法不能放行");
            Check(Run(J(1),task with{Required=false}).Verdict==InspectionVerdict.Error,"只有可选任务不能放行");
            Check(Run(J(1),task with{Rules=[task.Rules[0] with{PortId="missing"}]}).Verdict==InspectionVerdict.Error,"缺输出不能放行");
            Check(Run(J(1),task with{Rules=[task.Rules[0] with{Minimum=double.NaN}]}).Verdict==InspectionVerdict.Error,"无效阈值不能放行");
            Check(Run(J(1),task with{Rules=[task.Rules[0] with{Pointer="/missing"}]}).Verdict==InspectionVerdict.Error,"缺字段不能放行");
            Check(Run(J(1),task with{Rules=[task.Rules[0] with{Kind=(InspectionRuleKind)999}]}).Verdict==InspectionVerdict.Error,"未知规则不能放行");
            foreach(var code in new[]{"OK","NOT_DETECTED","DETECTED_NG","ERROR","UNKNOWN"})
            {
                var t=task with{Rules=[task.Rules[0] with{Kind=InspectionRuleKind.DetectionDecision,Pointer="/Code"}]};
                Check(Run(J(new{Code=code}),t).Verdict==(code=="OK"?InspectionVerdict.Ok:code is "NOT_DETECTED" or "DETECTED_NG"?InspectionVerdict.Ng:InspectionVerdict.Error),"检测状态 "+code);
            }
            foreach(var code in new[]{"MATCH_OK","NOT_FOUND","AMBIGUOUS","TIMEOUT","MODEL_INVALID","MATCH_REJECTED","OUT_OF_RANGE"})
            {
                var t=task with{Rules=[task.Rules[0] with{Kind=InspectionRuleKind.MatchStatus,Pointer="/Code"}]};
                Check(Run(J(new{Code=code}),t).Verdict==(code=="MATCH_OK"?InspectionVerdict.Ok:code=="NOT_FOUND"?InspectionVerdict.Ng:InspectionVerdict.Error),"匹配状态 "+code);
            }
            var missing=ProductInspectionEvaluator.Evaluate(flow,id,WorkflowRunStatus.Success,[],[]);
            Check(missing.Verdict==InspectionVerdict.Error,"预期任务节点完全缺失不能放行");
            Check(ProductInspectionEvaluator.Evaluate(flow with{InspectionTasks=[]},id,WorkflowRunStatus.Success,[],[]).Verdict==InspectionVerdict.NotConfigured,"旧方案未配置不默认OK");
            var bad=task with{Id=Guid.NewGuid(),Name="第二必检",Rules=[]};
            var mixed=flow with{InspectionTasks=[task,bad]};
            var badRun=ProductInspectionEvaluator.Evaluate(mixed,id,WorkflowRunStatus.Success,[new(node.Id,node.DisplayName,NodeExecutionStatus.Success,DateTime.UtcNow,DateTime.UtcNow,0,new Dictionary<string,JsonElement>{{"value",J(0)}},null)],[]);
            Check(badRun.Verdict==InspectionVerdict.Error&&badRun.Tasks.Any(t=>t.Verdict==InspectionVerdict.Ng),"ERROR优先于NG");
            var region=new InspectionRegion(task.RegionId,"区域",node.Id,"image",new(1,1,20,20));
            var saved=flow with{InspectionRegions=[region]};
            var store=new ProjectStore();var workspace=store.CreateProject(Path.Combine(output,"任务.hvproj"),"任务");store.SaveProject(workspace,[saved]);
            var restored=store.OpenProject(workspace.ProjectFilePath).Project;
            Check(restored.SchemaVersion==2&&restored.Flows.Single().SchemaVersion==2,"带任务方案升级为2防止旧程序静默丢弃");
            Check(restored.Flows.Single().InspectionTasks.Single().Rules.Single().NodeId==node.Id,"任务规则保存恢复");
            var copy=store.CopyFlow(workspace,saved,"副本");
            Check(copy.InspectionTasks.Single().NodeIds.Single()==copy.Nodes.Single().Id&&copy.InspectionRegions.Single().ImageNodeId==copy.Nodes.Single().Id,"复制重映射来源及任务节点");
            Check(copy.InspectionTasks.Single().RegionId==copy.InspectionRegions.Single().Id&&copy.InspectionTasks.Single().Rules.Single().NodeId==copy.Nodes.Single().Id,"复制重映射区域及规则");
            var exported=store.ExportFlow(saved,Path.Combine(output,"flow.json"));var imported=store.ImportFlow(workspace,exported);
            Check(imported.InspectionTasks.Single().Rules.Single().NodeId==imported.Nodes.Single().Id,"导入引用重映射");
            Check(ProductInspectionEvaluator.Evaluate(flow with{InspectionTasks=null!},id,WorkflowRunStatus.Success,[],[]).Verdict==InspectionVerdict.Error,"损坏任务集合得到ERROR");
            File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{checks=_checks,Passed},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS {_checks} 项纯受管判定及持久化检查");return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"error.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
}
