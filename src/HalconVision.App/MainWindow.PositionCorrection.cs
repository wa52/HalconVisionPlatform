using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;

namespace HalconVision.App;

public partial class MainWindow
{
    private async void TeachPosition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeParameterViewModel { IsPositionReference: true } parameter } ||
            _vm.SelectedFlow is not { } flow || flow.SelectedNode is not { TypeId: HalconNodeProvider.PositionCorrection } node) return;
        try
        {
            await StopExecutionAsync();
            if (_closing || _vm.SelectedFlow != flow || flow.SelectedNode?.Id != node.Id) return;
            var run = _vm.LastRun;
            var poseEdge = flow.Document.Edges.SingleOrDefault(edge => edge.ToNodeId == node.Id && edge.ToPortId == "poses");
            var imageEdge = flow.Document.Edges.SingleOrDefault(edge => edge.ToNodeId == node.Id && edge.ToPortId == "image");
            if (run?.FlowId != flow.Id || poseEdge is null || imageEdge is null ||
                run.RuntimeOutputs?.GetValueOrDefault(poseEdge.FromNodeId)?.GetValueOrDefault(poseEdge.FromPortId) is not PoseSet poses ||
                run.SpatialOutputs?.GetValueOrDefault(imageEdge.FromNodeId)?.GetValueOrDefault(imageEdge.FromPortId) is not { } frame)
                throw new InvalidOperationException("请先连接原始图像和快捷匹配位姿，并单次运行；未示教时矫正节点报错不影响获取上游匹配结果。");
            if (poses.ConfigurationIdentity != WorkflowConfiguration.NodeSignature(flow.Document, poseEdge.FromNodeId) ||
                frame.ConfigurationIdentity != WorkflowConfiguration.NodeSignature(flow.Document, imageEdge.FromNodeId))
                throw new InvalidOperationException("上次运行后配置已修改，请重新单次运行匹配。");
            var matchingNode = flow.Document.Nodes.Single(item => item.Id == poseEdge.FromNodeId);
            if (matchingNode.TypeId != HalconNodeProvider.QuickMatch || ShapeMatchModelCache.ReadModelIdentity(matchingNode) != poses.ModelIdentity)
                throw new InvalidOperationException("模板已变化，请重新运行匹配。");
            if (poses.SourceFrame is not { } poseFrame || frame.RunId != run.Id || poseFrame.RunId != run.Id || frame.FrameId != poseFrame.FrameId ||
                frame.SpaceId != poseFrame.SpaceId || frame.Annotated || poseFrame.Annotated || frame.Width != poseFrame.Width || frame.Height != poseFrame.Height)
                throw new InvalidOperationException("图像和匹配必须来自同一帧未标注的图像。");
            var reference = HalconPositionCorrectionExecutor.Teach(poses) with { ImageConfigurationIdentity = frame.ConfigurationIdentity };
            parameter.ValueText = JsonSerializer.Serialize(reference); parameter.Commit();
            if (parameter.Error is not null) throw new InvalidDataException(parameter.Error);
            DetectionSummary.Text = "基准已保存 · 重新运行以查看矫正图像";
            DetectionSummary.Foreground = System.Windows.Media.Brushes.LightGray;
            _vm.Log("位置矫正基准已示教；请在矫正图上配置固定检测 ROI，并保存项目。");
        }
        catch (Exception error) { ShowError("位置矫正示教失败", error); }
    }
    private async void ClearPosition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeParameterViewModel { IsPositionReference: true } parameter }) return;
        try
        {
            await StopExecutionAsync();
            if (_closing) return;
            parameter.ValueText = "null"; parameter.Commit();
            DetectionSummary.Text = "基准已清除 · 下次执行需重新示教"; _vm.Log("已清除位置矫正基准。");
        }
        catch (Exception error) { ShowError("清除基准失败", error); }
    }
}
