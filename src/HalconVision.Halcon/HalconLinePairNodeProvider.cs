using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconLinePairNodeProvider : INodeDefinitionProvider
{
    public const string LinePair = "halcon.line-pair-measure";
    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new(LinePair, 1, "几何测量", "线线测量",
            "连接同帧原图与两条拟合线，测量0～90°小夹角及有限线段最短距离；像素单位，公差在检测任务中设置。",
            [new("image", "原始图像", PortDirection.Input, "halcon.image", Required: true, RequiresUnannotatedImage: true),
             new("lineA", "线段 A", PortDirection.Input, "halcon.line2d", Required: true),
             new("lineB", "线段 B", PortDirection.Input, "halcon.line2d", Required: true),
             new("angle", "小夹角（0～90°）", PortDirection.Output, "number"),
             new("segmentDistance", "线段最短距离（像素）", PortDirection.Output, "number"),
             new("measurement", "完整测量数据", PortDirection.Output, "halcon.measurement2d"),
             new("overlay", "测量图形", PortDirection.Output, "halcon.geometry"),
             new("preview", "测量预览", PortDirection.Output, "halcon.image", Annotated: true),
             new("status", "测量状态", PortDirection.Output, "inspection.result")],
            [new("showLines", "显示拟合线段", "bool", false, JsonSerializer.SerializeToElement(true)),
             new("showConnector", "显示最短距离连线", "bool", false, JsonSerializer.SerializeToElement(true))],
            TaskRoiParameterId: "taskRoi");
    }
}
