using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.App;

public sealed record InspectionNodeChoice(Guid Id, string Name);
public sealed record InspectionPortChoice(string Id, string Name);
public sealed record InspectionSourceChoice(string Key, Guid NodeId, string PortId, string Name);
public sealed record InspectionRuleChoice(InspectionRuleKind Kind, string Name);
public sealed class InspectionRegionEdit : ObservableModel
{
    private string _name = "区域";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string SourceKey { get; set; } = "";
    public string RowBegin { get; set; } = "0"; public string ColumnBegin { get; set; } = "0";
    public string RowEnd { get; set; } = "100"; public string ColumnEnd { get; set; } = "100";
}
public sealed class InspectionRuleEdit : ObservableModel
{
    private Guid _nodeId;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NodeId { get => _nodeId; set { if (Set(ref _nodeId, value)) Raise(nameof(OutputChoices)); } }
    public Func<Guid,IReadOnlyList<InspectionPortChoice>>? OutputLookup { get; set; }
    public IReadOnlyList<InspectionPortChoice> OutputChoices => OutputLookup?.Invoke(NodeId) ?? [];
    public string PortId { get; set; } = "count"; public InspectionRuleKind Kind { get; set; }
    public string Pointer { get; set; } = ""; public string Minimum { get; set; } = "1";
    public string Maximum { get; set; } = "1"; public string Expected { get; set; } = "";
}
public sealed class InspectionTaskEdit : ObservableModel
{
    private string _name = "任务";
    public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get => _name; set => Set(ref _name, value); }
    public Guid RegionId { get; set; } public bool Required { get; set; } = true; public bool Enabled { get; set; } = true;
    public List<Guid> NodeIds { get; set; } = []; public ObservableCollection<InspectionRuleEdit> Rules { get; } = [];
}

public partial class InspectionTaskEditorWindow : Window
{
    private readonly FlowTabViewModel _flow;
    private InspectionTaskEdit? _editing;
    private readonly Dictionary<Guid,IReadOnlyList<InspectionPortChoice>> _outputs;
    public ObservableCollection<InspectionRegionEdit> Regions { get; } = [];
    public ObservableCollection<InspectionTaskEdit> Tasks { get; } = [];
    public IReadOnlyList<InspectionSourceChoice> Sources { get; }
    public IReadOnlyList<InspectionNodeChoice> Nodes { get; }
    public InspectionRegion? DrawRequest { get; private set; }
    public static IReadOnlyList<InspectionRuleChoice> RuleChoices { get; } = [new(InspectionRuleKind.NumberRange,"数值范围"),new(InspectionRuleKind.TextEquals,"文本相等"),new(InspectionRuleKind.BooleanTrue,"布尔为真"),new(InspectionRuleKind.DetectionDecision,"检测判定"),new(InspectionRuleKind.MatchStatus,"匹配可信")];
    public InspectionTaskEditorWindow(Window owner, FlowTabViewModel flow, IReadOnlyCollection<NodeDefinition> catalog, WorkflowExecutionRun? run)
    {
        Owner = owner; _flow = flow;
        _outputs=flow.Document.Nodes.ToDictionary(n=>n.Id,n=>(IReadOnlyList<InspectionPortChoice>)(catalog.FirstOrDefault(d=>d.TypeId==n.TypeId&&d.Version==n.TypeVersion)?.Ports.Where(p=>p.Direction==PortDirection.Output).Select(p=>new InspectionPortChoice(p.Id,p.Name)).ToArray()??[]));
        Sources = flow.Document.Nodes.SelectMany(n => catalog.First(d=>d.TypeId==n.TypeId&&d.Version==n.TypeVersion).Ports
            .Where(p=>p.Direction==PortDirection.Output&&p.DataType=="halcon.image"&&!p.Annotated)
            .Select(p=>new InspectionSourceChoice($"{n.Id}/{p.Id}",n.Id,p.Id,$"{n.DisplayName} · {p.Name}"))).ToArray();
        Nodes = flow.Document.Nodes.Where(n=>catalog.First(d=>d.TypeId==n.TypeId&&d.Version==n.TypeVersion) is {} d && (d.TaskRoiParameterId is not null || d.Parameters.Any(p=>p.DataType=="roiList"||p.Id=="roi"&&p.DataType=="roiDraw")))
            .Select(n=>new InspectionNodeChoice(n.Id,n.DisplayName)).ToArray();
        foreach(var r in flow.Document.InspectionRegions) Regions.Add(new(){Id=r.Id,Name=r.Name,SourceKey=$"{r.ImageNodeId}/{r.ImagePortId}",RowBegin=Number(r.Bounds.RowBegin),ColumnBegin=Number(r.Bounds.ColumnBegin),RowEnd=Number(r.Bounds.RowEnd),ColumnEnd=Number(r.Bounds.ColumnEnd)});
        foreach(var t in flow.Document.InspectionTasks)
        {
            var edit=new InspectionTaskEdit(){Id=t.Id,Name=t.Name,RegionId=t.RegionId,Required=t.Required,Enabled=t.Enabled,NodeIds=[..t.NodeIds]};
            foreach(var r in t.Rules)edit.Rules.Add(new(){Id=r.Id,NodeId=r.NodeId,PortId=r.PortId,Kind=r.Kind,Pointer=r.Pointer,Minimum=Number(r.Minimum),Maximum=Number(r.Maximum),Expected=r.Expected,OutputLookup=Outputs});
            Tasks.Add(edit);
        }
        InitializeComponent(); Resources.MergedDictionaries.Add(owner.Resources); DataContext=this;
        MemberList.ItemsSource=Nodes;
        RulesGrid.Columns.Add(new DataGridComboBoxColumn{Header="节点",ItemsSource=Nodes,DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValueBinding=new Binding("NodeId"),Width=150});
        var outputSelector=new FrameworkElementFactory(typeof(ComboBox));outputSelector.SetBinding(ItemsControl.ItemsSourceProperty,new Binding("OutputChoices"));
        outputSelector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,new Binding("PortId"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        outputSelector.SetValue(ItemsControl.DisplayMemberPathProperty,"Name");outputSelector.SetValue(System.Windows.Controls.Primitives.Selector.SelectedValuePathProperty,"Id");
        RulesGrid.Columns.Add(new DataGridTemplateColumn{Header="输出端口",CellTemplate=new DataTemplate{VisualTree=outputSelector},Width=130});
        RulesGrid.Columns.Add(new DataGridComboBoxColumn{Header="判定方式",ItemsSource=RuleChoices,DisplayMemberPath="Name",SelectedValuePath="Kind",SelectedValueBinding=new Binding("Kind"),Width=115});
        foreach(var pair in new[]{("字段","Pointer"),("下限","Minimum"),("上限","Maximum"),("期望文本","Expected")})RulesGrid.Columns.Add(new DataGridTextColumn{Header=pair.Item1,Binding=new Binding(pair.Item2),Width=90});
        RegionList.SelectedIndex=Regions.Count>0?0:-1; TaskList.SelectedIndex=Tasks.Count>0?0:-1;
        ResultText.Text=run?.FlowId==flow.Id&&run.Inspection is {} result ?
            (result.ConfigurationIdentity==InspectionPlan.ConfigurationIdentity(flow.Document)?"本轮结果":"历史结果 · 配置已更改，请重新运行")+
            $"\n产品：{result.Label}\n{string.Join("\n",result.Reasons)}\n\n"+string.Join("\n\n",result.Tasks.Select(t=>$"{t.Name} · {Verdict(t.Verdict)}\n{string.Join("\n",t.Reasons)}")) : "尚无本轮任务结果。";
    }
    private static string Number(double number)=>number.ToString("R",CultureInfo.InvariantCulture);
    private static string Verdict(InspectionVerdict verdict)=>verdict switch{InspectionVerdict.Ok=>"OK",InspectionVerdict.Ng=>"NG",InspectionVerdict.Error=>"ERROR",InspectionVerdict.Skipped=>"已跳过",_=>"未配置"};
    private static double Parse(string text)=>double.TryParse(text,System.Globalization.NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:throw new InvalidDataException("坐标及上下限必须为有限数字。");
    private void SaveSelection(){if(_editing is not null)_editing.NodeIds=MemberList.SelectedItems.Cast<InspectionNodeChoice>().Select(n=>n.Id).ToList();}
    private void Task_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {SaveSelection();_editing=TaskList.SelectedItem as InspectionTaskEdit;MemberList.SelectedItems.Clear();if(_editing is not null)foreach(var n in Nodes.Where(n=>_editing.NodeIds.Contains(n.Id)))MemberList.SelectedItems.Add(n);}
    private void AddRegion_Click(object sender,RoutedEventArgs e){var region=new InspectionRegionEdit{Name=$"区域 {Regions.Count+1}",SourceKey=Sources.FirstOrDefault()?.Key??""};Regions.Add(region);RegionList.SelectedItem=region;}
    private void RemoveRegion_Click(object sender,RoutedEventArgs e){if(RegionList.SelectedItem is InspectionRegionEdit r){if(Tasks.Any(t=>t.RegionId==r.Id)){Feedback.Text="该区域仍有任务引用，请先调整或移除对应任务。";return;}Regions.Remove(r);}}
    private void AddTask_Click(object sender,RoutedEventArgs e){var t=new InspectionTaskEdit{Name=$"任务 {Tasks.Count+1}",RegionId=Regions.FirstOrDefault()?.Id??Guid.Empty};Tasks.Add(t);TaskList.SelectedItem=t;}
    private void RemoveTask_Click(object sender,RoutedEventArgs e){if(TaskList.SelectedItem is InspectionTaskEdit t)Tasks.Remove(t);}
    private IReadOnlyList<InspectionPortChoice> Outputs(Guid id)=>_outputs.GetValueOrDefault(id)??[];
    private void AddRule_Click(object sender,RoutedEventArgs e)
    {
        SaveSelection();if(_editing is null)return;
        var nodeId=_editing.NodeIds.LastOrDefault();var outputs=Outputs(nodeId);var rule=new InspectionRuleEdit{NodeId=nodeId,OutputLookup=Outputs};
        if(_flow.Document.Nodes.FirstOrDefault(n=>n.Id==nodeId)?.TypeId==HalconVision.Halcon.HalconNodeProvider.StraightLineFind)
        {rule.PortId="status";rule.Kind=InspectionRuleKind.TextEquals;rule.Pointer="/Code";rule.Expected="FOUND";}
        else if(_flow.Document.Nodes.FirstOrDefault(n=>n.Id==nodeId)?.TypeId==HalconVision.Halcon.HalconCircleNodeProvider.CircleFind)
        {rule.PortId="status";rule.Kind=InspectionRuleKind.TextEquals;rule.Pointer="/Code";rule.Expected=CircleFindStatus.Found;}
        else if(_flow.Document.Nodes.FirstOrDefault(n=>n.Id==nodeId)?.TypeId==HalconVision.Halcon.HalconCirclePairNodeProvider.CirclePair)
        {rule.PortId="centerDistance";rule.Kind=InspectionRuleKind.NumberRange;rule.Pointer="";}
        else if(_flow.Document.Nodes.FirstOrDefault(n=>n.Id==nodeId)?.TypeId==HalconVision.Halcon.HalconLineCircleNodeProvider.LineCircle)
        {rule.PortId="boundaryToLine";rule.Kind=InspectionRuleKind.NumberRange;rule.Pointer="";}
        else if(_flow.Document.Nodes.FirstOrDefault(n=>n.Id==nodeId)?.TypeId==HalconVision.Halcon.HalconLinePairNodeProvider.LinePair)
        {rule.PortId="segmentDistance";rule.Kind=InspectionRuleKind.NumberRange;rule.Pointer="";}
        else if(outputs.Any(p=>p.Id=="decision")){rule.PortId="decision";rule.Kind=InspectionRuleKind.DetectionDecision;rule.Pointer="/Code";}
        else if(outputs.Any(p=>p.Id=="count"))rule.PortId="count";
        else if(outputs.Any(p=>p.Id=="area"))rule.PortId="area";
        else if(outputs.Any(p=>p.Id=="status")){rule.PortId="status";rule.Kind=InspectionRuleKind.MatchStatus;rule.Pointer="/Code";}
        else rule.PortId=outputs.FirstOrDefault()?.Id??"";
        _editing.Rules.Add(rule);
    }
    private void RemoveRule_Click(object sender,RoutedEventArgs e){if(_editing is not null&&RulesGrid.SelectedItem is InspectionRuleEdit r)_editing.Rules.Remove(r);}
    private IReadOnlyList<InspectionRegion> ReadRegions()=>Regions.Select(r=>{var source=Sources.Single(s=>s.Key==r.SourceKey);return new InspectionRegion(r.Id,r.Name.Trim(),source.NodeId,source.PortId,new(Parse(r.RowBegin),Parse(r.ColumnBegin),Parse(r.RowEnd),Parse(r.ColumnEnd)));}).ToArray();
    private bool Apply(bool draft)
    {
        try
        {
            Keyboard.ClearFocus();RulesGrid.CommitEdit(DataGridEditingUnit.Cell,true);RulesGrid.CommitEdit(DataGridEditingUnit.Row,true);SaveSelection();
            var regions=ReadRegions();var tasks=Tasks.Select(t=>new InspectionTask(t.Id,t.Name.Trim(),t.RegionId,[..t.NodeIds],t.Rules.Select(r=>new InspectionRule(r.Id,r.NodeId,r.PortId.Trim(),r.Kind,r.Pointer.Trim(),Parse(r.Minimum),Parse(r.Maximum),r.Expected)).ToList(),t.Required,t.Enabled)).ToArray();
            var candidate=_flow.Document with{InspectionRegions=regions.ToList(),InspectionTasks=tasks.ToList()};var issues=_flow.ValidateDocument(candidate);
            if(issues.Count>0&&!draft){Feedback.Text=string.Join("\n",issues.Select(i=>i.Message));return false;}
            _flow.SetInspectionPlan(regions,tasks);Feedback.Text=issues.Count>0?"草稿已应用，配置尚不完整，运行将被阻止。":"任务配置已应用，请保存项目并重新运行。";return true;
        }
        catch(Exception error){Feedback.Text="配置未应用："+error.Message;return false;}
    }
    private void Apply_Click(object sender,RoutedEventArgs e)=>Apply(false);
    private void DrawRegion_Click(object sender,RoutedEventArgs e){if(RegionList.SelectedItem is not InspectionRegionEdit region||!Apply(true))return;DrawRequest=ReadRegions().Single(r=>r.Id==region.Id);Close();}
}
