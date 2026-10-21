using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.App;

public partial class MainWindow
{
    private Action<Rectangle2D>? _inspectionRegionDraw;
    private InspectionRegion? _inspectionOutlineRegion;
    private async void ConfigureInspectionTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _vm.SelectedFlow is not { } flow) return;
        try
        {
            await StopExecutionAsync(); FinishRoiDrawing();
            var dialog = new InspectionTaskEditorWindow(this, flow, _vm.NodeCatalog, _vm.LastRun);
            dialog.ShowDialog();
            if (dialog.DrawRequest is { } region && _vm.SelectedFlow == flow && !_closing) await DrawInspectionRegionAsync(flow, region);
        }
        catch (Exception error) { ShowError("检测任务配置失败", error); }
    }

    private async Task DrawInspectionRegionAsync(FlowTabViewModel flow, InspectionRegion region)
    {
        var signature = WorkflowConfiguration.NodeSignature(flow.Document, region.ImageNodeId);
        var frame = _vm.LastRun?.SpatialOutputs?.GetValueOrDefault(region.ImageNodeId)?.GetValueOrDefault(region.ImagePortId);
        if (_vm.LastRun?.FlowId != flow.Id || frame?.ConfigurationIdentity != signature)
            await _vm.RunImageSourceAsync(region.ImageNodeId);
        if (_closing || _vm.SelectedFlow != flow) return;
        var run = _vm.LastRun;
        frame = run?.SpatialOutputs?.GetValueOrDefault(region.ImageNodeId)?.GetValueOrDefault(region.ImagePortId);
        if (frame is null || frame.Annotated || frame.Count != 1 ||
            run?.RuntimeOutputs?.GetValueOrDefault(region.ImageNodeId)?.GetValueOrDefault(region.ImagePortId) is not HImage image)
            throw new InvalidDataException("请先获取绑定来源的单幅未标注图像。");
        FinishRoiDrawing();
        image.GetImageSize(out _previewImageWidth, out _previewImageHeight);
        _imagePresenter.Display(image, PreviewWindow.HalconWindow, _previewImageWidth, _previewImageHeight, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        flow.SelectNode(region.ImageNodeId, false);
        ShowTemplateDrawingArea();
        ImageAreaTitle.Text = "任务区域绘制区";
        _inspectionRegionDraw = bounds =>
        {
            if (_vm.SelectedFlow != flow || _vm.LastRun?.Id != run.Id || WorkflowConfiguration.NodeSignature(flow.Document, region.ImageNodeId) != signature)
                throw new InvalidDataException("图像或流程配置已经变化，请重新绘制。");
            flow.SetInspectionPlan(flow.Document.InspectionRegions.Select(r => r.Id == region.Id ? r with { Bounds = bounds } : r), flow.Document.InspectionTasks);
            _vm.Log($"任务区域 {region.Name} 已更新，请保存项目并重新运行。");
        };
        _isDrawingRoi = true; _roiParameter = null;
        PreviewEmptyText.Visibility = Visibility.Collapsed;
        RoiDrawingHintText.Text = $"绘制任务区域：{region.Name} · 松开完成，Esc 取消";
        RoiDrawingHint.Visibility = Visibility.Visible; RoiDrawingOverlay.Visibility = Visibility.Visible;
        RoiDrawingOverlay.IsHitTestVisible = true; RoiDrawingOverlay.ClipToBounds = true;
        RoiDrawingOverlay.Cursor = Cursors.Cross; RoiDrawingOverlay.Focusable = true; RoiDrawingOverlay.Focus();
        DetectionSummary.Text = "正在绘制任务区域 · 完成后在“检测任务”中配置算法分支和规则"; DetectionSummary.Foreground = Brushes.LightGray;
    }
}
