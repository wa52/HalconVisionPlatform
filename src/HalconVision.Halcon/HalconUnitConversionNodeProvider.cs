using System.Text.Json;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconUnitConversionNodeProvider : INodeDefinitionProvider
{
    public const string ScaleCalibration = "halcon.pixel-scale";
    public const string LengthConversion = "halcon.length-to-mm";
    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new(ScaleCalibration, 1, "标定与单位", "比例标定",
            "固定同相机、镜头及物平面下，用已知标准长度/像素长度建立等比例。先输入参考图像尺寸并确认；不补偿畸变、透视或矫正图。",
            [new("image", "未缩放原图", PortDirection.Input, "halcon.image", Required:true, RequiresUnannotatedImage:true),
             new("calibration", "像素毫米比例", PortDirection.Output, "vision.pixel-scale"),
             new("mmPerPixel", "毫米/像素", PortDirection.Output, "number", Unit:"mm/px"),
             new("status", "标定状态", PortDirection.Output, "inspection.result")],
            [P("referencePixels","标准件像素长度（px）",0d), P("referenceMillimeters","标准件实际长度（mm）",0d),
             P("imageWidth","参考原图宽度（px）",0), P("imageHeight","参考原图高度（px）",0),
             new("confirmed","已确认同成像条件、同平面及等比例", "bool",true,JsonSerializer.SerializeToElement(false))]);
        yield return new(LengthConversion,1,"标定与单位","长度换算",
            "将同帧声明为 px 的长度或有符号净距转为 mm。只接受像素长度端口，不接受角度、面积、数量或已换算毫米值；公差在检测任务中设置。",
            [new("pixels","像素长度/净距",PortDirection.Input,"number",Required:true,Unit:"px"),
             new("calibration","同帧标定比例",PortDirection.Input,"vision.pixel-scale",Required:true),
             new("lengthMm","长度/净距（mm）",PortDirection.Output,"number",Unit:"mm"),
             new("result","完整换算数据",PortDirection.Output,"vision.unit-result"),
             new("status","换算状态",PortDirection.Output,"inspection.result")],[],TaskRoiParameterId:"taskRoi");
    }
    private static NodeParameterDefinition P<T>(string id,string name,T value)=>new(id,name,"number",true,JsonSerializer.SerializeToElement(value));
}
