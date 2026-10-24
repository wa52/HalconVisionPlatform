using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconCirclePairNodeProvider : INodeDefinitionProvider
{
    public const string CirclePair = "halcon.circle-pair-measure";
    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new(CirclePair, 1, "几何测量", "圆圆测量",
            "连接同帧原图和两个圆，测量圆心距、圆周最短距离；单位像素，公差使用检测任务的数值范围规则。",
            [new("image", "原始图像", PortDirection.Input, "halcon.image", Required: true, RequiresUnannotatedImage: true),
             new("circleA", "圆 A", PortDirection.Input, "halcon.circle2d", Required: true),
             new("circleB", "圆 B", PortDirection.Input, "halcon.circle2d", Required: true),
             new("centerDistance", "圆心距（像素）", PortDirection.Output, "number"),
             new("boundaryDistance", "圆周最短距离（像素）", PortDirection.Output, "number"),
             new("measurement", "完整测量数据", PortDirection.Output, "halcon.measurement2d"),
             new("overlay", "测量图形", PortDirection.Output, "halcon.geometry"),
             new("preview", "测量预览", PortDirection.Output, "halcon.image", Annotated: true),
             new("status", "测量状态", PortDirection.Output, "inspection.result")],
            [new("showCenterLine", "显示圆心连线", "bool", false, JsonSerializer.SerializeToElement(true)),
             new("showBoundaryLine", "显示圆周最短连线", "bool", false, JsonSerializer.SerializeToElement(true))],
            TaskRoiParameterId: "taskRoi");
    }
}
