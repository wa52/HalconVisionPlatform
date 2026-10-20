using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using HalconVision.Halcon;
using HalconVision.Domain;
using HalconVision.Application;

namespace HalconVision.App;

public partial class MainWindow
{
    private bool _capturingTemplate;
    private GridLength? _templateDrawingBottomHeight;
    private double _templateDrawingFlowMinimum;

    private void ShowTemplateDrawingArea()
    {
        if (!_bottomVisible) ToggleBottom_Click(this, new RoutedEventArgs());
        _templateDrawingBottomHeight = BottomRow.Height;
        _templateDrawingFlowMinimum = WorkspaceGrid.RowDefinitions[0].MinHeight;
        WorkspaceGrid.RowDefinitions[0].MinHeight = 120;
        BottomRow.Height = new GridLength(Math.Max(150, WorkspaceGrid.ActualHeight * .68));
        RuntimeResultsPanel.Visibility = RuntimeSplitter.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(RuntimeImagePanel, 3);
        ImageAreaTitle.Text = "模板绘制区";
        WorkspaceGrid.UpdateLayout();
        ImageFit_Click(this, new RoutedEventArgs());
    }

    private void RestoreTemplateDrawingArea()
    {
        if (_templateDrawingBottomHeight is not { } height) return;
        _templateDrawingBottomHeight = null;
        BottomRow.Height = height;
        WorkspaceGrid.RowDefinitions[0].MinHeight = _templateDrawingFlowMinimum;
        RuntimeResultsPanel.Visibility = RuntimeSplitter.Visibility = Visibility.Visible;
        Grid.SetColumnSpan(RuntimeImagePanel, 1);
        ImageAreaTitle.Text = "节点图像";
        WorkspaceGrid.UpdateLayout();
        ImageFit_Click(this, new RoutedEventArgs());
    }
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
        if (sender is not Button { Tag: NodeParameterViewModel parameter } || !parameter.IsQuickMatchTemplateRoi ||
            _capturingTemplate || _closing || _switchingProject || _vm.SelectedFlow is not { } flow || flow.SelectedNode is not { TypeId: HalconNodeProvider.QuickMatch } node) return;
        _capturingTemplate = true;
        ((Button)sender).IsEnabled = false;
        try
        {
            await StopExecutionAsync();
            if (_closing || _switchingProject || !ReferenceEquals(_vm.SelectedFlow, flow) || flow.SelectedNode?.Id != node.Id) return;
            if (!HasCurrentQuickMatchInput(flow, node))
            {
                DetectionSummary.Text = "正在执行上游节点，获取模板图像…";
                var upstreamTask = _vm.RunTemplateUpstreamAsync(node.Id);
                _executionTask = upstreamTask;
                try { await upstreamTask; }
                finally { if (ReferenceEquals(_executionTask, upstreamTask)) _executionTask = null; }
                if (_closing || _switchingProject || !ReferenceEquals(_vm.SelectedFlow, flow) || flow.SelectedNode?.Id != node.Id) return;
            }
            SaveQuickMatchInputAsTemplate(flow, node);
        }
        catch (OperationCanceledException) { _vm.Log("模板示教已取消。"); }
        catch (Exception error) { if (!_closing) ShowError("采集模板失败", error); }
        finally { _capturingTemplate = false; ((Button)sender).IsEnabled = true; }
    }

    private void SaveQuickMatchInputAsTemplate(FlowTabViewModel flow, FlowNodeViewModel node)
    {
        using var image = CopyQuickMatchInput(flow, node);
        var workspace = _vm.Workspace ?? throw new InvalidOperationException("请先打开项目。");
        var relative = $"assets/templates/{workspace.Project.Id:N}/{Guid.NewGuid():N}.png";
        var path = Path.Combine(workspace.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new IOException("模板快照路径已存在，请重新示教。");
        image.WriteImage("png", 0, path);
        flow.SetAssetReferences(flow.Document.AssetReferences.Append(relative).ToArray());
        var parameter = node.Parameters.Single(item => item.IsQuickMatchTemplate);
        parameter.ValueText = path; parameter.Commit();
        var roi = node.Parameters.Single(item => item.Definition.Id == "templateRoi");
        roi.ValueText = ""; roi.Commit();
        _vm.ReleaseModelSessions();
        ShowQuickMatchTemplate(node);
        _vm.RefreshProjectTree();
        _vm.Log("已接收上游当前帧并固定为项目模板；模板范围已重置，请绘制范围。切换待检图片不会自动更换模板。");
    }

    // The first run prepares teaching only. Never report self-matching the template frame as a detection pass.
    private async Task<bool> PrepareMissingQuickMatchTemplateAsync(FlowTabViewModel flow)
    {
        var missing = flow.Nodes.Where(n => n.Enabled && n.TypeId == HalconNodeProvider.QuickMatch &&
            string.IsNullOrWhiteSpace(n.Parameters.Single(p => p.IsQuickMatchTemplate).ValueText)).ToArray();
        if (missing.Length == 0) return false;
        var missingIds = missing.Select(n => n.Id).ToHashSet();
        bool HasMissingAncestor(Guid nodeId, HashSet<Guid> visited)
        {
            if (!visited.Add(nodeId)) throw new InvalidDataException("上游图像连接存在循环。");
            foreach (var edge in flow.Document.Edges.Where(e => e.ToNodeId == nodeId))
                if (missingIds.Contains(edge.FromNodeId) || HasMissingAncestor(edge.FromNodeId, new(visited))) return true;
            return false;
        }
        var node = missing.FirstOrDefault(n => !HasMissingAncestor(n.Id, []))
            ?? throw new InvalidDataException("未配置模板的匹配节点存在循环依赖。");
        flow.SelectNode(node.Id, false);
        DetectionSummary.Text = "首次运行 · 正在接收上游图像以示教模板…";
        if (!HasCurrentQuickMatchInput(flow, node)) await _vm.RunTemplateUpstreamAsync(node.Id);
        if (_closing || _switchingProject || !ReferenceEquals(_vm.SelectedFlow, flow)) return true;
        SaveQuickMatchInputAsTemplate(flow, node);
        BeginRoiDrawing(node.Parameters.Single(p => p.Definition.Id == "templateRoi"), null, preparingTemplate: true);
        DetectionSummary.Text = "已接收上游图像 · 拖动框选模板范围，完成后再次运行匹配";
        _vm.ProjectStatus = "等待模板示教 · 当前仅获取图像，尚未执行匹配";
        _vm.Log("首次运行已获取上游模板；请框选模板范围，再次运行。未执行本帧匹配。");
        return true;
    }

    private bool HasCurrentQuickMatchInput(FlowTabViewModel flow, FlowNodeViewModel node)
    {
        var edge = flow.Document.Edges.SingleOrDefault(item => item.ToNodeId == node.Id && item.ToPortId == "image");
        return edge is not null && _vm.LastRun is { } run && run.FlowId == flow.Id &&
            run.NodeResults.Any(result => result.NodeId == edge.FromNodeId && result.Status == NodeExecutionStatus.Success) &&
            run.SpatialOutputs?.GetValueOrDefault(edge.FromNodeId)?.GetValueOrDefault(edge.FromPortId) is { } frame &&
            frame.RunId == run.Id && frame.ConfigurationIdentity == WorkflowConfiguration.NodeSignature(flow.Document, edge.FromNodeId) &&
            run.RuntimeOutputs?.GetValueOrDefault(edge.FromNodeId)?.GetValueOrDefault(edge.FromPortId) is HImage image &&
            image.IsInitialized() && image.CountObj() == 1;
    }

    private HImage CopyQuickMatchInput(FlowTabViewModel flow, FlowNodeViewModel node)
    {
        var edge = flow.Document.Edges.SingleOrDefault(item => item.ToNodeId == node.Id && item.ToPortId == "image")
            ?? throw new InvalidOperationException("请先连接快捷匹配的 Image 输入。");
        if (HasCurrentQuickMatchInput(flow, node) && _vm.LastRun is { } run && run.RuntimeOutputs?.TryGetValue(edge.FromNodeId, out var outputs) == true &&
            outputs.TryGetValue(edge.FromPortId, out var value) && value is HImage image && image.IsInitialized() && image.CountObj() > 0)
            return image.SelectObj(1);
        throw new InvalidOperationException("没有与当前配置一致的上游单帧图像，请重新执行上游后示教模板。");
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
        DetectionSummary.Text = "固定模板 · 拖动绘制模板范围；更换模板请取上游当前帧";
        DetectionSummary.Foreground = System.Windows.Media.Brushes.LightGray;
    }
}
