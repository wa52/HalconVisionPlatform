using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using HalconVision.App;

internal static class Program
{
 [STAThread]
 private static int Main()
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
 private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
 {
  for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
  { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
 }
}
