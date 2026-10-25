using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconLineCircleNodeProvider : INodeDefinitionProvider
{
    public const string LineCircle = "halcon.line-circle-measure";
    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new(LineCircle, 1, "几何测量", "线圆测量",
            "连接同帧原图、拟合直线及圆，按无限直线测量垂距和圆周间隙；公差使用检测任务的数值范围规则，单位像素。",
            [new("image", "原始图像", PortDirection.Input, "halcon.image", Required: true, RequiresUnannotatedImage: true),
             new("line", "直线几何（按延长线）", PortDirection.Input, "halcon.line2d", Required: true),
             new("circle", "圆几何", PortDirection.Input, "halcon.circle2d", Required: true),
             new("centerToLine", "圆心到直线（像素）", PortDirection.Output, "number", Unit:"px"),
             new("boundaryToLine", "圆周到直线（像素）", PortDirection.Output, "number", Unit:"px"),
             new("signedClearance", "圆周净距（负值为穿过）", PortDirection.Output, "number", Unit:"px"),
             new("measurement", "完整测量数据", PortDirection.Output, "halcon.measurement2d"),
             new("overlay", "测量图形", PortDirection.Output, "halcon.geometry"),
             new("preview", "测量预览", PortDirection.Output, "halcon.image", Annotated: true),
             new("status", "测量状态", PortDirection.Output, "inspection.result")],
            [new("showNormal", "显示圆心垂线", "bool", false, JsonSerializer.SerializeToElement(true)),
             new("showBoundaryLine", "显示圆周最短连线", "bool", false, JsonSerializer.SerializeToElement(true))],
            TaskRoiParameterId: "taskRoi");
    }
}
