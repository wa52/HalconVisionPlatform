using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace HalconVision.App;

/// <summary>Presentation only: command labels remain intact for bindings and automation.</summary>
public static class IconUi
{
    public static readonly DependencyProperty KeepTextProperty = DependencyProperty.RegisterAttached(
        "KeepText", typeof(bool), typeof(IconUi), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetKeepText(DependencyObject target) => (bool)target.GetValue(KeepTextProperty);
    public static void SetKeepText(DependencyObject target, bool value) => target.SetValue(KeepTextProperty, value);
}

public sealed class ActionIconConverter : IMultiValueConverter
{
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    private static readonly IReadOnlyDictionary<string, (string Glyph, string Help)> Actions =
        new Dictionary<string, (string, string)>
        {
            ["文件"] = ("\uE8A5", "文件：新建、打开、保存项目及退出"),
            ["项目"] = ("\uE8B7", "项目：新建、导入和导出流程"),
            ["新建项目…"] = ("\uE8A5", "新建空白项目文件"),
            ["打开项目…"] = ("\uE8B7", "打开项目文件并恢复流程"),
            ["保存项目"] = ("\uE74E", "保存当前项目与流程（Ctrl+S）"),
            ["新建流程…"] = ("\uE710", "在当前项目中新建空白流程"),
            ["导入流程…"] = ("\uE8B5", "从文件导入流程"),
            ["导出当前流程…"] = ("\uE898", "将当前流程导出到文件"),
            ["选文件…"] = ("\uE8A5", "选择文件"),
            ["选文件夹…"] = ("\uE8B7", "选择文件夹"),
            ["退出"] = ("\uE7E8", "退出软件并释放运行资源"),
            ["确定"] = ("\uE73E", "确定并应用输入内容"),
            ["取消"] = ("\uE711", "取消当前操作"),
            ["取当前图像为模板"] = ("\uE722", "接收上游当前帧建立模板；无有效帧时执行上游，自动保存在项目内。切换待检图片不更换模板"),
            ["编辑"] = ("\uE70F", "编辑：撤销、重做及节点操作"),
            ["视图"] = ("\uE7F4", "视图：检测显示配置及面板开关"),
            ["运行"] = ("\uE768", "运行：执行或停止流程"),
            ["帮助"] = ("\uE897", "帮助与版本信息"),
            ["撤销"] = ("\uE7A7", "撤销（Ctrl+Z）"),
            ["重做"] = ("\uE7A6", "重做（Ctrl+Y）"),
            ["复制节点"] = ("\uE8C8", "复制选中节点（Ctrl+C）"),
            ["粘贴节点"] = ("\uE77F", "粘贴节点（Ctrl+V）"),
            ["删除节点 (Delete)"] = ("\uE74D", "删除选中节点（Delete）"),
            ["删除节点"] = ("\uE74D", "删除选中节点（Delete）"),
            ["添加所选节点"] = ("\uE710", "将节点库中选中的节点添加到当前流程"),
            ["单次运行"] = ("\uE768", "单次运行：执行当前流程一次"),
            ["运行流程"] = ("\uE768", "执行当前流程一次"),
            ["连续运行"] = ("\uE8EE", "连续运行：循环执行当前流程，文件夹图像逐张切换"),
            ["停止"] = ("\uE71A", "停止执行并释放采集及模型会话资源"),
            ["停止运行"] = ("\uE71A", "停止当前流程"),
            ["检测模式"] = ("\uE7F4", "进入检测模式：仅显示已配置的图像和检测结果"),
            ["返回编辑"] = ("\uE70F", "返回编辑模式：编辑节点、参数和显示配置"),
            ["适应画布"] = ("\uE9A6", "调整画布缩放以显示全部节点"),
            ["−"] = ("\uE71F", "缩小"),
            ["+"] = ("\uE8A3", "放大"),
            ["适应"] = ("\uE9A6", "适应图像窗口"),
            ["适应窗口"] = ("\uE9A6", "适应图像窗口"),
            ["独立放大"] = ("\uE8A7", "在独立窗口中查看图像"),
            ["检测显示配置…"] = ("\uE713", "配置检测面板、底图、叠加节点和显示字段"),
            ["节点库面板"] = ("\uE8F1", "显示或隐藏节点库面板"),
            ["属性面板"] = ("\uE713", "显示或隐藏节点属性面板"),
            ["下方调试面板"] = ("\uE9D9", "显示或隐藏图像与调试面板"),
            ["关于 HALCON 节点平台"] = ("\uE946", "查看平台和 HALCON 运行环境信息"),
            ["扫描"] = ("\uE72C", "扫描所选 HALCON 接口的相机设备"),
            ["连接"] = ("\uE839", "连接选中的相机设备"),
            ["断开"] = ("\uE8CD", "断开相机并释放采集句柄"),
            ["绘制矩形范围"] = ("\uE7A8", "在节点图像中拖动绘制矩形检测范围"),
            ["清除范围"] = ("\uE75C", "清除已绘制的矩形范围；重新运行后使用节点默认范围"),
            ["设为基准"] = ("\uE81D", "把本轮唯一匹配位置设为矫正基准；配置修改后请先重新运行"),
            ["清除基准"] = ("\uE75C", "清除位置矫正基准，下次执行需重新示教"),
            ["新增 ROI"] = ("\uE710", "新增 ROI：在图像中拖动绘制矩形"),
            ["重绘选中"] = ("\uE70F", "重新绘制选中 ROI 的检测范围"),
            ["删除选中"] = ("\uE74D", "删除选中的 ROI"),
            ["清空"] = ("\uE75C", "清空 ROI 列表，恢复全图推理"),
            ["添加面板"] = ("\uE8F0", "添加一个检测显示面板"),
            ["添加内容"] = ("\uE710", "在面板中添加图像、输出参数或检测结果"),
            ["删除面板"] = ("\uE74D", "删除当前显示面板"),
            ["移除内容"] = ("\uE711", "移除此显示内容"),
            ["复制数据"] = ("\uE8C8", "复制选中字段的完整数据"),
            ["运行结果"] = ("\uE9D9", "查看节点运行状态、检测结论和耗时"),
            ["节点输出"] = ("\uE8A5", "查看节点输出参数及完整数据"),
            ["运行日志"] = ("\uE81C", "查看运行日志和错误信息"),
            ["性能信息"] = ("\uE9D9", "查看流程性能信息"),
            ["端口信息（高级）"] = ("\uE8D4", "展开或折叠输入输出端口及类型"),
            ["相机连接控制"] = ("\uE722", "展开或折叠相机接口、设备和连接控制"),
            ["叠加节点与显示字段（勾选）"] = ("\uE81E", "展开或折叠叠加节点、图形、判定和显示字段"),
            ["高级数据"] = ("\uE943", "展开或折叠几何坐标及高级输出数据"),
            ["图形"] = ("\uE91B", "显示此节点的检测图形"),
            ["判定"] = ("\uE73E", "显示此节点的检测判定"),
        };

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var content = values.Length > 0 ? values[0] : null;
        if ((values.Length > 1 && values[1] is true) || content is not string label || !Actions.TryGetValue(label, out var action))
            return content ?? "";
        return new TextBlock
        {
            Text = action.Glyph, FontFamily = IconFont, FontSize = 17, FontWeight = FontWeights.Normal,
            MinWidth = 20, MinHeight = 20, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    internal static string? Help(object value) => value is string label
        ? Actions.TryGetValue(label, out var action) ? action.Help : label : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ActionHelpConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (object?)ActionIconConverter.Help(value) ?? DependencyProperty.UnsetValue;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
