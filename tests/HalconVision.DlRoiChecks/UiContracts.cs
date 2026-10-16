using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class UiContracts
{
    public static void CheckDashboardActivation(string scheme, string output)
    {
        var store = new ProjectStore(); var source = store.OpenProject(scheme);
        var reader = source.Project.Flows.Single().Nodes.Single(node => node.TypeId == HalconNodeProvider.ImageRead);
        var flow = new HalconVision.Domain.FlowDocument { Name = "显示切换检查", Nodes = [reader], DetectionPanels =
            [new(Guid.NewGuid(), reader.Id, Contents: [new(Guid.NewGuid(), reader.Id, Overlays: [new(reader.Id)])])] };
        var path = System.IO.Path.Combine(output, "显示切换检查.hvproj");
        store.SaveProject(new(output, source.Project with { Flows = [flow], DefaultFlowId = flow.Id.ToString("N") }, path), [flow]);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new MainWindow(path);
            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var vm = (WorkflowWorkspaceViewModel)window.DataContext;
                    var dashboard = Field<DetectionDashboard>(window, "DetectionDashboard");
                    dashboard.SetSource(vm.SelectedFlow, () => vm.LastRun); dashboard.EnsureInitialPanels();
                    window.UpdateLayout(); await Task.Delay(200);
                    var run = await vm.RunSelectedFlowAsync(); vm.SetExecutionRun(run);
                    var bounds = new HalconVision.Domain.Rectangle2D(20, 20, 180, 100);
                    ((Dictionary<string, object?>)run.RuntimeOutputs![reader.Id])["roiOverlay"] = new HalconVision.Domain.MultiRoiOverlay(
                        [new(Guid.NewGuid(), "同位结果一", bounds, "A"), new(Guid.NewGuid(), "同位结果二", bounds, "B")]);
                    dashboard.SetSource(vm.SelectedFlow, () => vm.LastRun);
                    dashboard.Visibility = Visibility.Visible;
                    window.UpdateLayout(); await Task.Delay(200);
                    var view = dashboard.Tiles.Single().Contents.Single();
                    if (Field<int>(view, "_width") <= 0)
                        throw new InvalidOperationException("先运行再显示检测面板：底图没有刷新。");
                    var labels = Field<Canvas>(view, "_overlayCanvas").Children.OfType<TextBlock>().Where(text => text.Text.Contains("同位结果")).ToArray();
                    if (labels.Length != 2 || Canvas.GetTop(labels[0]) == Canvas.GetTop(labels[1]))
                        throw new InvalidOperationException("相同 ROI 的多条结果标注互相覆盖。");
                    vm.SelectedFlow!.MarkSaved();
                }
                catch (Exception ex) { failure = ex; }
                finally { ((WorkflowWorkspaceViewModel)window.DataContext).SelectedFlow?.MarkSaved(); window.Close(); }
            };
            window.Show(); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("显示契约未在 30 秒内完成。");
        if (failure is not null) throw failure;
    }
    public static void CheckRoiLayer(string scheme)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var vm = (WorkflowWorkspaceViewModel)window.DataContext;
                vm.OpenProject(scheme);
                var parameter = vm.SelectedFlow!.Nodes.First(node => node.TypeId == HalconNodeProvider.Classification).Parameters.Single(item => item.IsRoiList);
                typeof(MainWindow).GetMethod("SetRoiOutlineParameter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [parameter]);
                if (Field<Canvas>(window, "RoiDrawingOverlay").Visibility != Visibility.Visible)
                    throw new InvalidOperationException("绘制完成后叠加层仍为 Collapsed，无法进入布局并恢复 ROI 框。");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    ((WorkflowWorkspaceViewModel)window.DataContext).Dispose();
                    Field<HSmartWindowControlWPF>(window, "PreviewWindow").Dispose();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
}
