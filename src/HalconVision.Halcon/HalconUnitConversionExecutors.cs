using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconPixelScaleExecutor : INodeExecutor
{
    public string TypeId => HalconUnitConversionNodeProvider.ScaleCalibration;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if(context.RuntimeInputs?.GetValueOrDefault("image") is not HImage image || !image.IsInitialized() || image.CountObj()!=1)
            throw new InvalidDataException("比例标定需要本轮一张未缩放原图。");
        image.GetImageSize(out int width,out int height);
        var frame=UnitConversionGuard.Frame(context,"image");
        if(frame.Width!=width||frame.Height!=height)throw new InvalidDataException("CALIBRATION_SIZE_MISMATCH：帧尺寸与原图不符。");
        if(!context.Node.Parameters.TryGetValue("confirmed",out var confirmed)||confirmed.ValueKind!=JsonValueKind.True)
            throw new InvalidDataException("CALIBRATION_NOT_CONFIRMED：请使用标准件填写长度与参考尺寸，并确认同成像条件、同平面及等比例。");
        var pixels=Number("referencePixels");var millimeters=Number("referenceMillimeters");
        if(Number("imageWidth")!=width||Number("imageHeight")!=height)
            throw new InvalidDataException("CALIBRATION_SIZE_MISMATCH：当前原图尺寸与标定参考不一致，请重新标定。");
        if(pixels>Math.Sqrt(Math.Pow(width-1d,2)+Math.Pow(height-1d,2)))throw new InvalidDataException("参考像素长度超过原图对角线，请核对标准件直线长度。");
        var scale=new PixelScaleCalibration(pixels,millimeters,millimeters/pixels,width,height,true);
        UnitConversionGuard.Validate(scale);
        var status=new UnitConversionStatus(UnitConversionStatus.ScaleReady,"比例已配置 · 固定等比例原图");
        context.Log(status.Message);context.CancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutionOutput(
            new Dictionary<string,JsonElement>{{"calibration",JsonSerializer.SerializeToElement(scale)},{"mmPerPixel",JsonSerializer.SerializeToElement(scale.MillimetersPerPixel)},{"status",JsonSerializer.SerializeToElement(status)}},
            new Dictionary<string,object?>{{"calibration",scale},{"mmPerPixel",scale.MillimetersPerPixel},{"status",status}}));
        double Number(string key)=>context.Node.Parameters.TryGetValue(key,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number)&&double.IsFinite(number)&&number>0
            ?number:throw new InvalidDataException("标定参数必须为有限正数："+key);
    }
}

public sealed class HalconLengthToMillimeterExecutor : INodeExecutor
{
    public string TypeId=>HalconUnitConversionNodeProvider.LengthConversion;
    public int Version=>1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var frame=UnitConversionGuard.Frame(context,"calibration");
        var rawScale=Read("calibration");
        var scale=rawScale is PixelScaleCalibration typed?typed:rawScale is JsonElement {ValueKind:JsonValueKind.Object} json?json.Deserialize<PixelScaleCalibration>():null;
        if(scale is null)throw new InvalidDataException("CALIBRATION_REQUIRED：请连接本轮比例标定输出。");
        UnitConversionGuard.Validate(scale);
        if(frame.Width!=scale.Width||frame.Height!=scale.Height)throw new InvalidDataException("CALIBRATION_SIZE_MISMATCH：标定数据与帧尺寸不符。");
        // A missing upstream length has no frame metadata. Its null is preserved,
        // never replaced by an old serialized value or a fabricated zero.
        var raw=Read("pixels");
        PixelLengthConversion? converted=null;
        if(raw is not null && raw is not JsonElement {ValueKind:JsonValueKind.Null or JsonValueKind.Undefined})
        {
            var lengthFrame=UnitConversionGuard.Frame(context,"pixels");
            if((frame.RunId,frame.FrameId,frame.SpaceId,frame.SourceNodeId,frame.SourcePortId,frame.Width,frame.Height)!=(lengthFrame.RunId,lengthFrame.FrameId,lengthFrame.SpaceId,lengthFrame.SourceNodeId,lengthFrame.SourcePortId,lengthFrame.Width,lengthFrame.Height))
                throw new InvalidDataException("CALIBRATION_FRAME_MISMATCH：长度与标定必须来自本轮同帧原图。");
            var pixels=raw switch{double d=>d,int i=>i,long l=>l,JsonElement {ValueKind:JsonValueKind.Number} value when value.TryGetDouble(out var d)=>d,_=>throw new InvalidDataException("像素长度类型错误。")};
            var mm=pixels*scale.MillimetersPerPixel;
            if(!double.IsFinite(pixels)||!double.IsFinite(mm)||pixels!=0&&mm==0)throw new InvalidDataException("换算值非有限、溢出或下溢，未输出毫米值。");
            converted=new(pixels,mm,scale,scale.Width,scale.Height);
        }
        if(context.Node.Parameters.TryGetValue("taskRoi",out var roi))
            _=TaskRoiScope.Read(context.Node with{Parameters=new(context.Node.Parameters){["roi"]=roi}},scale.Width,scale.Height)
                ?? throw new InvalidDataException("换算任务区域无效。");
        var status=converted is null?new UnitConversionStatus(UnitConversionStatus.Missing,"上游像素长度缺失，未生成毫米结果"):
            new UnitConversionStatus(UnitConversionStatus.Converted,"长度已换算为 mm");
        context.Log(status.Message);context.CancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutionOutput(
            new Dictionary<string,JsonElement>{{"lengthMm",JsonSerializer.SerializeToElement(converted?.Millimeters)},{"result",JsonSerializer.SerializeToElement(converted)},{"status",JsonSerializer.SerializeToElement(status)}},
            new Dictionary<string,object?>{{"lengthMm",converted?.Millimeters},{"result",converted},{"status",status}}));
        object? Read(string key)=>context.RuntimeInputs?.TryGetValue(key,out var runtime)==true?runtime:
            context.Inputs.TryGetValue(key,out var value)?value:null;
    }
}

internal static class UnitConversionGuard
{
    public static FrameReference Frame(NodeExecutionContext context,string port)
    {
        var frame=context.InputFrames?.GetValueOrDefault(port);
        if(frame is null||context.RunId==Guid.Empty||frame.RunId!=context.RunId||frame.FrameId==Guid.Empty||frame.SpaceId==Guid.Empty||frame.SourceNodeId==Guid.Empty||string.IsNullOrWhiteSpace(frame.SourcePortId)||frame.Count!=1||frame.ImageIndex!=1||frame.Annotated||frame.Corrected||frame.Width<=0||frame.Height<=0)
            throw new InvalidDataException("CALIBRATION_FRAME_REQUIRED：需要本轮单帧、未标注未矫正原图的长度及标定来源："+port);
        return frame;
    }
    public static void Validate(PixelScaleCalibration scale)
    {
        if(!scale.IsotropicConfirmed||scale.Model!="fixed-isotropic-raw-v1"||scale.Width<=0||scale.Height<=0||
            !new[]{scale.ReferencePixels,scale.ReferenceMillimeters,scale.MillimetersPerPixel}.All(v=>double.IsFinite(v)&&v>0)||
            scale.MillimetersPerPixel!=scale.ReferenceMillimeters/scale.ReferencePixels)
            throw new InvalidDataException("CALIBRATION_INVALID：比例未确认、无效或内部参数不一致。");
    }
}
