using System.Text.Json;
using HalconVision.Domain;
using HalconVision.Persistence;

if (args.Length != 1 || Directory.Exists(args[0])) return 2;
var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value,string text) { if(!value)throw new Exception(text); checks.Add(text); }
void Reject(Action action,string text) { try { action(); } catch(Exception error) when(error is IOException or InvalidDataException) {checks.Add(text);return;} throw new Exception(text); }
try
{
    var store=new WorkflowRunArchiveStore();var node=new NodeInstance{DisplayName="测量",TypeId="fixture"};var flow=new FlowDocument{Nodes=[node]};
    var now=DateTime.UtcNow;var project=Guid.NewGuid();
    WorkflowExecutionRun Run(WorkflowRunStatus state)=>new(Guid.NewGuid(),flow.Id,now,now,state,
        [new(node.Id,node.DisplayName,NodeExecutionStatus.Success,now,now,1,new Dictionary<string,JsonElement>{{"length",JsonSerializer.SerializeToElement(15d)}},null)],[],[],
        RuntimeOutputs:new Dictionary<Guid,IReadOnlyDictionary<string,object?>>{{node.Id,new Dictionary<string,object?>{{"native",new NativeProbe()}}}});
    foreach(var state in Enum.GetValues<WorkflowRunStatus>())
    {
        using var run=Run(state);var path=store.Save(root,project,"检查项目",flow,run);var restored=store.Read(path);
        Check(restored.RunId==run.Id&&restored.Status==state&&restored.NodeResults[0].Outputs["length"].GetDouble()==15,"结果恢复 "+state);
        Check(restored.Flow.Nodes[0].Id==node.Id,"配置快照恢复 "+state);
        var before=File.ReadAllBytes(path);Reject(()=>store.Save(root,project,"检查项目",flow,run),"同轮归档拒绝覆盖 "+state);Check(before.SequenceEqual(File.ReadAllBytes(path)),"旧归档保持完整 "+state);
        var envelope=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(path))!;
        envelope["Data"]=JsonSerializer.SerializeToElement(envelope["Data"].GetString()!.Replace("检查项目","篡改项目"));
        // Serializer escapes Chinese, so use an ASCII value that is definitely present.
        envelope["Data"]=JsonSerializer.SerializeToElement(envelope["Data"].GetString()!.Replace("15","16"));
        var changed=Path.Combine(root,Guid.NewGuid()+".json");File.WriteAllText(changed,JsonSerializer.Serialize(envelope));Reject(()=>store.Read(changed),"篡改数据拒绝 "+state);
    }
    using(var run=Run(WorkflowRunStatus.Success))
    {
        Reject(()=>store.Save(root,project,"检查项目",flow with{Id=Guid.NewGuid()},run),"跨流程拒绝");
        Reject(()=>store.Save(root,Guid.Empty,"检查项目",flow,run),"空项目身份拒绝");
        Reject(()=>store.Save(root,project,"检查项目",flow,run with{NodeResults=[run.NodeResults[0] with{Status=NodeExecutionStatus.Running}]}),"未完成节点拒绝");
    }
    var damaged=Path.Combine(root,"damaged.json");File.WriteAllText(damaged,"{");Reject(()=>store.Read(damaged),"坏JSON拒绝");
    Check(!Directory.GetFiles(root,"*.tmp").Any(),"没有残留临时文件");
    File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(new{count=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"PASS {checks.Count} 项归档检查");return 0;
}
catch(Exception error){File.WriteAllText(Path.Combine(root,"error.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
sealed class NativeProbe { public string Value=>throw new Exception("原生运行数据不应被序列化"); }
