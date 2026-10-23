using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

/// <summary>Registered separately so existing HALCON plugin hosts need not register unrelated new executors.</summary>
public sealed class HalconCircleNodeProvider : INodeDefinitionProvider
{
    public const string CircleFind = "halcon.circle-find";
    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new(CircleFind, 1, "特征检测", "圆查找",
            "在框选范围内，用 HALCON 2D Metrology 测量参考圆附近的边缘，输出原图圆心、半径和直径；不是全图自动搜圆。",
            [new("image", "原始图像", PortDirection.Input, "halcon.image", Required: true, RequiresUnannotatedImage: true),
             new("circle", "圆几何", PortDirection.Output, "halcon.circle2d"),
             new("diameter", "直径（像素）", PortDirection.Output, "number"),
             new("score", "拟合评分", PortDirection.Output, "number"),
             new("residualRms", "径向残差（像素）", PortDirection.Output, "number"),
             new("edgePoints", "实际边缘点", PortDirection.Output, "halcon.point-set"),
             new("count", "找到数量", PortDirection.Output, "number"),
             new("status", "查找状态", PortDirection.Output, "inspection.result"),
             new("overlay", "圆与测量图形", PortDirection.Output, "halcon.geometry"),
             new("preview", "检测预览", PortDirection.Output, "halcon.image", Annotated: true)],
            [P("roi", "矩形搜索范围", "roiDraw", ""),
             P("referenceRadius", "参考半径（像素；0按框估计）", "number", 0d),
             P("searchHalfWidth", "径向搜索半宽（像素）", "number", 10d),
             P("measureThreshold", "边缘阈值", "number", 20d),
             P("measureTransition", "边缘极性（从圆内向外）", "edgeTransition", "all"),
             P("minScore", "最小拟合评分（边缘利用率）", "number", .6d),
             P("numMeasures", "测量卡尺数量", "number", 48, true),
             P("measureLength2", "卡尺切向半长（像素）", "number", 3d, true),
             P("measureSigma", "边缘平滑 σ", "number", 1d, true),
             P("minimumRadius", "结果最小半径（像素）", "number", 1d, true),
             P("maximumRadius", "结果最大半径（像素）", "number", 100000d, true),
             P("maximumResidual", "最大径向残差（像素）", "number", 2d, true),
             P("showSearchRegion", "显示搜索框", "bool", true, true),
             P("showFittedCircle", "显示拟合圆及圆心", "bool", true, true),
             P("showDiagnostics", "显示卡尺与实际边缘点", "bool", false, true)]);
    }
    private static NodeParameterDefinition P<T>(string id, string name, string type, T value, bool advanced = false) =>
        new(id, name, type, id != "roi", JsonSerializer.SerializeToElement(value), advanced);
}
