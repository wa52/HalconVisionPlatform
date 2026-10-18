using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HalconVision.Domain;
using HalconVision.Halcon;

namespace HalconVision.App;

/// <summary>Edits the node parameter for future run snapshots; never mutates an in-flight HALCON model.</summary>
public sealed class DetectionThresholdControl : WrapPanel
{
    private readonly ComboBox _nodes = new() { Width = 175, DisplayMemberPath = "Name", Margin = new Thickness(4, 0, 8, 0) };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1, TickFrequency = .1, Width = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _value = new() { Width = 65, Margin = new Thickness(8, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _apply = new() { Content = "应用阈值", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 8, 0) };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private FlowTabViewModel? _flow;
    private Func<WorkflowExecutionRun?> _run = () => null;
    private List<Choice> _choices = [];
    private bool _sync, _draft;

    public DetectionThresholdControl()
    {
        Margin = new Thickness(8, 2, 8, 7); Visibility = Visibility.Collapsed;
        ToolTip = "选择目标检测节点，设置置信度阈值后点击应用。值越大筛选越严格；当前图像不会重新推理，连续运行从下一轮生效。";
        System.Windows.Automation.AutomationProperties.SetAutomationId(_nodes, "DetectionThresholdNode");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_slider, "DetectionThresholdSlider");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_value, "DetectionThresholdValue");
        System.Windows.Automation.AutomationProperties.SetName(_value, "目标检测置信度阈值");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_apply, "ApplyDetectionThreshold");
        Children.Add(new TextBlock { Text = "目标检测阈值", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(_nodes); Children.Add(_slider); Children.Add(_value); Children.Add(_apply); Children.Add(_status);
        _nodes.SelectionChanged += (_, _) => { if (!_sync) { _draft = false; RefreshValue(); } };
        _slider.ValueChanged += (_, _) =>
        {
            if (_sync) return;
            _sync = true; _value.Text = _slider.Value.ToString("0.000", CultureInfo.InvariantCulture); _sync = false;
            _draft = true; ValidateDraft();
        };
        _value.TextChanged += (_, _) => { if (!_sync) { _draft = true; ValidateDraft(); } };
        _value.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        _apply.Click += (_, _) => Apply();
    }

    public void SetSource(FlowTabViewModel? flow, Func<WorkflowExecutionRun?> run)
    {
        var selected = ReferenceEquals(flow, _flow) ? (_nodes.SelectedItem as Choice)?.Id : null;
        if (!ReferenceEquals(flow, _flow)) _draft = false;
        _flow = flow; _run = run;
        var choices = flow?.Nodes.Where(node => node.TypeId == HalconNodeProvider.ObjectDetection).Select(node => new Choice(node.Id, node.DisplayName)).ToList() ?? [];
        Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_choices.SequenceEqual(choices))
        {
            _sync = true; _choices = choices; _nodes.ItemsSource = choices;
            _nodes.SelectedItem = choices.FirstOrDefault(node => node.Id == selected) ?? choices.FirstOrDefault(); _sync = false;
            if (selected != (_nodes.SelectedItem as Choice)?.Id) _draft = false;
        }
        RefreshValue();
    }

    private void RefreshValue()
    {
        if (SelectedNode() is not { } node) { _apply.IsEnabled = false; return; }
        var value = node.Parameters.TryGetValue("minimumConfidence", out var configured) && configured.ValueKind == JsonValueKind.Number && configured.TryGetDouble(out var n) ? n : .5;
        if (!_draft)
        {
            _sync = true; _value.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            _slider.Value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0; _sync = false;
        }
        ValidateDraft();
    }

    private NodeInstance? SelectedNode() => _nodes.SelectedItem is Choice selected
        ? _flow?.Document.Nodes.FirstOrDefault(node => node.Id == selected.Id && node.TypeId == HalconNodeProvider.ObjectDetection) : null;

    private bool ReadDraft(out double value) => double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0 && value <= 1;

    private void ValidateDraft()
    {
        var valid = ReadDraft(out var value) && SelectedNode() is not null;
        _apply.IsEnabled = valid;
        _status.Foreground = valid ? Brushes.LightGray : Brushes.OrangeRed;
        if (!valid) { _status.Text = "请输入大于 0、且不大于 1 的数值，例如 0.5"; return; }
        _sync = true; _slider.Value = value; _sync = false;
        if (_draft) { _status.Text = "待应用 · 点击应用或按 Enter；下一轮生效"; return; }
        var recent = _run();
        var result = recent?.FlowId == _flow?.Id ? recent?.NodeResults.FirstOrDefault(node => node.NodeId == SelectedNode()!.Id) : null;
        var actual = result?.Outputs.TryGetValue("modelInfo", out var info) == true && info.TryGetProperty("minimumConfidence", out var used) && used.TryGetDouble(out var number)
            ? number.ToString("0.###", CultureInfo.InvariantCulture) : "未运行";
        _status.Text = $"配置 {value.ToString("0.###", CultureInfo.InvariantCulture)} · 最近结果阈值 {actual}";
    }

    private void Apply()
    {
        if (!ReadDraft(out var value) || SelectedNode() is not { } node || _flow is null) { ValidateDraft(); return; }
        var parameter = _flow.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Parameters.FirstOrDefault(item => item.Definition.Id == "minimumConfidence");
        if (parameter is null) { _status.Text = "节点已删除或参数不可用，请重新选择"; return; }
        parameter.ValueText = value.ToString("R", CultureInfo.InvariantCulture); parameter.Commit();
        if (parameter.Error is not null) { _status.Text = parameter.Error; _status.Foreground = Brushes.OrangeRed; return; }
        _draft = false; RefreshValue();
    }

    private sealed record Choice(Guid Id, string Name);
}
