using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;
using HalconVision.App;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;

internal static class Program
{
 [STAThread]
 private static int Main()
 {
  try { return RunChecks(); }
  catch (Exception error) { Console.Error.WriteLine(error); return 1; }
 }
 private static int RunChecks()
 {
  var app = new App(); app.InitializeComponent();
  var window = new MainWindow();
  var root = (FrameworkElement)window.Content;
  root.Measure(new Size(1180, 680)); root.Arrange(new Rect(0, 0, 1180, 680)); root.UpdateLayout();
  // Exercise long names without changing any project file or reading images.
  foreach (var text in Descendants(root).OfType<TextBlock>().Where(t =>
      BindingOperations.GetBindingExpression(t, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "ProjectName"))
      text.Text = new string('长', 80);
  var failures = new List<string>(); var checks = 0;
  var iconConverter = new ActionIconConverter();
  var helpConverter = new ActionHelpConverter();
  foreach (var label in new[] { "文件", "项目", "新建项目…", "打开项目…", "保存项目", "新建流程…", "导入流程…", "导出当前流程…", "选文件…", "选文件夹…", "退出", "确定", "取消", "取当前图像为模板", "设为基准", "清除基准" })
  {
      checks += 2;
      if (iconConverter.Convert(new object[] { label, false }, typeof(object), null!, CultureInfo.InvariantCulture) is not TextBlock { FontFamily.Source: "Segoe MDL2 Assets" })
          failures.Add("未显示文件操作图标：" + label);
      if (helpConverter.Convert(label, typeof(object), null!, CultureInfo.InvariantCulture) is not string { Length: > 0 })
          failures.Add("文件操作缺少功能提示：" + label);
  }
  foreach (var width in new[] { 1180d, 1366d, 1580d })
  {
   root.Measure(new Size(width, 680)); root.Arrange(new Rect(0, 0, width, 680)); root.UpdateLayout();
   foreach (var button in Descendants(root).OfType<ButtonBase>().Where(b => b.Visibility == Visibility.Visible && b.ActualWidth > 0 && b.Content is string))
   {
    var parent = VisualTreeHelper.GetParent(button);
    while (parent is not null && parent is not Border) parent = VisualTreeHelper.GetParent(parent);
    if (parent is not FrameworkElement boundary) continue;
    Check(button, boundary, button.Content?.ToString() ?? "", width);
   }
   foreach (var text in Descendants(root).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && t.ActualWidth > 0 && t.Text.StartsWith("选中节点后按")))
   {
    var parent = VisualTreeHelper.GetParent(text);
    while (parent is not null && parent is not Border) parent = VisualTreeHelper.GetParent(parent);
    if (parent is FrameworkElement boundary) Check(text, boundary, "画布工具提示", width);
   }
  }
  var registry = new NodeRegistry(); registry.Register(new PositionDefinition(), [new HalconPositionCorrectionExecutor()]);
  var definition = registry.Definitions.Single();
  var positionNode = new NodeInstance { TypeId = definition.TypeId, DisplayName = "位置矫正",
      Parameters = definition.Parameters.ToDictionary(p => p.Id, p => p.DefaultValue!.Value) };
  var positionFlow = new FlowTabViewModel(new FlowDocument { Nodes = [positionNode] }, registry, new WorkflowGraphValidator(registry));
  var workspaceVm = (WorkflowWorkspaceViewModel)window.DataContext;
  workspaceVm.Flows.Add(positionFlow); workspaceVm.SelectedFlow = positionFlow; positionFlow.SelectNode(positionNode.Id, false);
  foreach (var width in new[] { 1180d, 1366d, 1580d })
  {
      root.Measure(new Size(width, 680)); root.Arrange(new Rect(0, 0, width, 680)); root.UpdateLayout();
      var teach = Descendants(root).OfType<Button>().Single(b => b.Content as string == "设为基准" && InVisibleBranch(b));
      var clear = Descendants(root).OfType<Button>().Single(b => b.Content as string == "清除基准" && InVisibleBranch(b));
      var boundary = (FrameworkElement)window.FindName("RightPanel");
      var scroller = Descendants(boundary).OfType<ScrollViewer>().First();
      var relative = teach.TranslatePoint(new Point(), scroller);
      scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + relative.Y + Math.Max(teach.ActualHeight, clear.ActualHeight) + 8 - scroller.ViewportHeight));
      root.UpdateLayout();
      Check(teach, boundary, "位置矫正示教", width); Check(clear, boundary, "位置矫正清除", width);
      checks++;
      var a = teach.TranslatePoint(new Point(), boundary); var b = clear.TranslatePoint(new Point(), boundary);
      if (new Rect(a, teach.RenderSize).IntersectsWith(new Rect(b, clear.RenderSize))) failures.Add("示教与清除控件重叠");
  }
  var quickRegistry = new NodeRegistry(); quickRegistry.Register(new QuickDefinition(), [new HalconQuickMatchExecutor()]);
  var quickDefinition=quickRegistry.Definitions.Single();
  var quickNode=new NodeInstance{TypeId=quickDefinition.TypeId,DisplayName="快捷匹配",Parameters=quickDefinition.Parameters.ToDictionary(p=>p.Id,p=>p.DefaultValue!.Value)};
  var quickFlow=new FlowTabViewModel(new FlowDocument{Nodes=[quickNode]},quickRegistry,new WorkflowGraphValidator(quickRegistry));
  workspaceVm.Flows.Add(quickFlow);workspaceVm.SelectedFlow=quickFlow;quickFlow.SelectNode(quickNode.Id,false);
  foreach(var width in new[]{1180d,1366d,1580d})
  {
      root.Measure(new Size(width,680));root.Arrange(new Rect(0,0,width,680));root.UpdateLayout();
      var node=quickFlow.SelectedNode!;
      checks++;if(node.PrimaryParameters.Count()!=5||node.AdvancedParameters.Count()!=14)failures.Add("快捷匹配基础/高级参数分组异常");
      var advanced=Descendants(root).OfType<Expander>().Single(e=>System.Windows.Automation.AutomationProperties.GetName(e)=="高级匹配参数");
      checks++;if(advanced.IsExpanded)failures.Add("高级匹配参数未默认折叠");
      advanced.IsExpanded=true;root.UpdateLayout();
      var boundary=(FrameworkElement)window.FindName("RightPanel");var scroller=Descendants(boundary).OfType<ScrollViewer>().First();
      scroller.ScrollToEnd();root.UpdateLayout();
      foreach(var button in Descendants(advanced).OfType<Button>().Where(b=>InVisibleBranch(b)&&b.ActualWidth>0))
      {checks++;var point=button.TranslatePoint(new Point(),boundary);if(point.X<-.5||point.X+button.ActualWidth>boundary.ActualWidth+.5)failures.Add("高级匹配按钮横向越界");}
      checks++;if(scroller.ScrollableHeight<=0)failures.Add("高级参数缺少滚动空间");
      advanced.IsExpanded=false;scroller.ScrollToHome();root.UpdateLayout();
  }
  var viewer = new ImageViewerWindow("layout-check.png");
  ((TextBlock)viewer.FindName("ImageNameText")).Text = new string('长', 100);
  var viewerRoot = (FrameworkElement)viewer.Content;
  viewerRoot.Measure(new Size(700, 470)); viewerRoot.Arrange(new Rect(0, 0, 700, 470)); viewerRoot.UpdateLayout();
  foreach (var button in Descendants(viewerRoot).OfType<Button>())
  {
      var parent = VisualTreeHelper.GetParent(button);
      while (parent is not null && parent is not Border) parent = VisualTreeHelper.GetParent(parent);
      if (parent is FrameworkElement boundary) Check(button, boundary, "独立图像：" + button.Content, 700);
  }
  ((IDisposable)viewer.FindName("ImageWindow")).Dispose();
  ((IDisposable)window.FindName("PreviewWindow")).Dispose();
  // No workflow or camera operation is started by this layout-only probe.
  ((WorkflowWorkspaceViewModel)window.DataContext).Dispose();
  if (checks == 0) throw new InvalidOperationException("没有实际布局检查项");
  Console.WriteLine($"布局检查数：{checks}");
  foreach (var failure in failures.Distinct()) Console.WriteLine(failure);
  Console.WriteLine(failures.Count == 0 ? "PASS: 操作控件未超出所属区域" : $"FAIL: {failures.Count} 处控件越界");
  return failures.Count == 0 ? 0 : 1;

  void Check(FrameworkElement element, FrameworkElement boundary, string name, double width)
  {
   checks++; var origin = element.TranslatePoint(new Point(), boundary);
   if (origin.X < -.5 || origin.Y < -.5 || origin.X + element.ActualWidth > boundary.ActualWidth + .5 || origin.Y + element.ActualHeight > boundary.ActualHeight + .5)
    failures.Add($"{width}: {name} bounds=({origin.X:F1},{origin.Y:F1},{element.ActualWidth:F1},{element.ActualHeight:F1}) parent=({boundary.ActualWidth:F1},{boundary.ActualHeight:F1})");
  }
 }
 private sealed class PositionDefinition : INodeDefinitionProvider
 {
     public IEnumerable<NodeDefinition> GetDefinitions() => new HalconNodeProvider().GetDefinitions().Where(d => d.TypeId == HalconNodeProvider.PositionCorrection);
 }
 private sealed class QuickDefinition : INodeDefinitionProvider
 {
     public IEnumerable<NodeDefinition> GetDefinitions()=>new HalconNodeProvider().GetDefinitions().Where(d=>d.TypeId==HalconNodeProvider.QuickMatch);
 }
 private static bool InVisibleBranch(DependencyObject element)
 {
     for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
         if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
     return true;
 }
 private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
 {
  for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
  { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
 }
}
