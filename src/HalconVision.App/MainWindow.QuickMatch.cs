using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using HalconVision.Halcon;
using HalconVision.Domain;
using Microsoft.Win32;

namespace HalconVision.App;

public partial class MainWindow
{
    private static IReadOnlyList<DetectionRoi> ReadConfiguredDisplayRois(NodeParameterViewModel parameter)
    {
        if (parameter.IsRoiList) return parameter.ReadRois();
        var parts = parameter.ValueText.Split(',');
        var numbers = parts.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN).ToArray();
        var label = parameter.Definition.Id == "templateRoi" ? "模板范围" : parameter.Definition.Id == "roi" ? "搜索范围" : parameter.Name;
        return numbers.Length == 4 && numbers.All(double.IsFinite)
            ? [new DetectionRoi(Guid.Empty, label, numbers[0], numbers[1], numbers[2], numbers[3])] : [];
    }
    private async void CaptureTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeParameterViewModel { IsQuickMatchTemplate: true } parameter } ||
            _vm.SelectedFlow is not { } flow || flow.SelectedNode is not { TypeId: HalconNodeProvider.QuickMatch } node) return;
        try
        {
            await StopExecutionAsync();
            if (_closing || !ReferenceEquals(_vm.SelectedFlow, flow) || flow.SelectedNode?.Id != node.Id) return;
            using var image = CopyQuickMatchInput(flow, node);
            var dialog = new SaveFileDialog { Title = "保存当前图像作为匹配模板", Filter = "PNG 模板图像|*.png", DefaultExt = ".png", AddExtension = true, FileName = "匹配模板.png" };
            if (dialog.ShowDialog(this) != true) return;
            image.WriteImage("png", 0, dialog.FileName);
            parameter.ValueText = Path.GetFullPath(dialog.FileName); parameter.Commit();
            var roi = node.Parameters.Single(item => item.Definition.Id == "templateRoi");
            roi.ValueText = ""; roi.Commit();
            _vm.ReleaseModelSessions();
            ShowQuickMatchTemplate(node);
            _vm.Log("已保存模板图像；模板范围已重置，可绘制模板范围后运行匹配。");
        }
        catch (Exception error) { ShowError("采集模板失败", error); }
    }

    private HImage CopyQuickMatchInput(FlowTabViewModel flow, FlowNodeViewModel node)
    {
        var edge = flow.Document.Edges.SingleOrDefault(item => item.ToNodeId == node.Id && item.ToPortId == "image")
            ?? throw new InvalidOperationException("请先连接快捷匹配的 Image 输入。");
        if (_vm.LastRun is { } run && run.FlowId == flow.Id && run.RuntimeOutputs?.TryGetValue(edge.FromNodeId, out var outputs) == true &&
            outputs.TryGetValue(edge.FromPortId, out var value) && value is HImage image && image.IsInitialized() && image.CountObj() > 0)
            return image.SelectObj(1);
        var source = flow.Document.Nodes.Single(item => item.Id == edge.FromNodeId);
        if (source.TypeId == HalconNodeProvider.ImageRead && source.Parameters.TryGetValue("path", out var pathValue))
        {
            var path = pathValue.GetString();
            var selected = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? EnumerateImageFiles(path).FirstOrDefault() : path;
            if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected)) return new HImage(selected);
        }
        throw new InvalidOperationException("请先运行上游节点生成图像；文件图像也可先配置有效路径。");
    }

    private void ShowQuickMatchTemplate(FlowNodeViewModel node)
    {
        var path = node.Parameters.Single(item => item.Definition.Id == "templateImage").ValueText;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("请先选择模板图像，再绘制模板范围。", path);
        FinishRoiDrawing();
        _previewImagePath = path;
        var dimensions = _imagePresenter.Display(path, PreviewWindow.HalconWindow);
        _previewImageWidth = dimensions.Width; _previewImageHeight = dimensions.Height;
        PreviewEmptyText.Visibility = Visibility.Collapsed;
        ImageFit_Click(this, new RoutedEventArgs());
        DetectionSummary.Text = "模板图像 · 拖动绘制模板范围；搜索范围在输入图像上绘制";
    }
}
