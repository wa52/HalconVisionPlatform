using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconPositionCorrectionExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.PositionCorrection;
    public int Version => 1;
    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.RuntimeInputs?.GetValueOrDefault("image") is not HImage source || !source.IsInitialized())
            throw Error("IMAGE_REQUIRED", "请连接本轮的原始图像。");
        if (source.CountObj() != 1) throw Error("SINGLE_IMAGE_REQUIRED", "位置矫正每轮需要一张图像，请使用文件夹逐张运行。");
        if (context.RuntimeInputs.GetValueOrDefault("poses") is not PoseSet poses) throw Error("POSE_REQUIRED", "请连接快捷匹配的位姿端口。");
        var frame = context.InputFrames?.GetValueOrDefault("image") ?? throw Error("FRAME_REQUIRED", "输入缺少图像来源标识。");
        var poseFrame = poses.SourceFrame;
        if (poseFrame is null || frame.RunId != context.RunId || poseFrame.RunId != context.RunId || frame.FrameId != poseFrame.FrameId ||
            frame.SpaceId != poseFrame.SpaceId || frame.Width != poseFrame.Width || frame.Height != poseFrame.Height || frame.Count != 1 || frame.ImageIndex != 1 || poseFrame.ImageIndex != 1)
            throw Error("FRAME_MISMATCH", "图像和位姿必须来自本轮同一帧及坐标空间。");
        if (frame.Annotated || poseFrame.Annotated) throw Error("ANNOTATED_IMAGE", "请使用原始图像，不能使用已绘制轮廓的匹配预览。");
        var current = UniquePose(poses);
        if (!context.Node.Parameters.TryGetValue(PositionReferenceParameters.Key, out var value) || value.ValueKind == JsonValueKind.Null)
            throw Error("REFERENCE_REQUIRED", "尚未示教，请先单次运行匹配，再点击“设为基准”。");
        var reference = value.Deserialize<PositionReference>() ?? throw Error("REFERENCE_REQUIRED", "基准数据无效，请重新示教。");
        ValidatePose(reference.Pose);
        if (reference.MatchNodeId != poses.NodeId || reference.ModelIdentity != poses.ModelIdentity || reference.ImageConfigurationIdentity != frame.ConfigurationIdentity ||
            !string.IsNullOrEmpty(reference.PoseImageConfigurationIdentity) && reference.PoseImageConfigurationIdentity != poseFrame.ConfigurationIdentity)
            throw Error("REFERENCE_STALE", "模板、建模范围或图像输入配置已变化，请重新示教。");
        source.GetImageSize(out int width, out int height);
        if (width != frame.Width || height != frame.Height || width != reference.Width || height != reference.Height)
            throw Error("SIZE_MISMATCH", "输入尺寸与示教基准不一致。");
        using var type = source.GetImageType(); using var channels = source.CountChannels();
        if (type.S != "byte" || channels.I is not (1 or 3)) throw Error("IMAGE_TYPE_UNSUPPORTED", "首版位置矫正支持 byte 灰度或 RGB 图像。");
        var rowOffset = current.Row - reference.Pose.Row; var columnOffset = current.Column - reference.Pose.Column;
        var angleOffset = NormalizeAngle(current.AngleDegrees - reference.Pose.AngleDegrees);
        if (QuickMatchParameters.Boolean(context.Node, "limitMotion", false))
        {
            var distance = QuickMatchParameters.Number(context.Node, "maximumTranslation", 100, 0, 1000000);
            var angle = QuickMatchParameters.Number(context.Node, "maximumRotation", 30, 0, 180);
            if (Math.Sqrt(rowOffset * rowOffset + columnOffset * columnOffset) > distance || Math.Abs(angleOffset) > angle)
                throw Error("POSE_OUT_OF_RANGE", "本轮位移或旋转超出节点配置的限制。");
        }
        using var matrix = PositionTransform.Standard(current, reference.Pose);
        HOperatorSet.HomMat2dInvert(matrix, out HTuple inverse); using var inverseScope = inverse;
        using var imageMatrix = PositionTransform.ForPixels(matrix);
        HImage? image = null; HRegion? valid = null;
        try
        {
            using var warped = PositionTransform.Warp(source, imageMatrix, width, height);
            using var domain = source.GetDomain(); using var transformedDomain = PositionTransform.WarpRegion(domain, imageMatrix, width, height);
            using var canvas = new HRegion(0d, 0d, height - 1d, width - 1d);
            valid = transformedDomain.Intersection(canvas);
            image = warped.ReduceDomain(valid);
            context.CancellationToken.ThrowIfCancellationRequested();
            var transform = new CoordinateTransform2D(matrix.DArr, inverse.DArr, frame.SpaceId, context.Node.Id);
            var ports = new Dictionary<string, JsonElement>
            {
                ["image"] = JsonSerializer.SerializeToElement(new { width, height, count = 1, channels = channels.I }),
                ["validRegion"] = JsonSerializer.SerializeToElement(new { width, height, count = 1 }),
                ["transform"] = JsonSerializer.SerializeToElement(transform),
                ["offsets"] = JsonSerializer.SerializeToElement(new { current, reference = reference.Pose, rowOffset, columnOffset, angleOffset, score = current.Score }),
                ["status"] = JsonSerializer.SerializeToElement(new { Code = "CORRECTION_OK", Message = $"位置矫正完成 · Δ行 {rowOffset:F2} · Δ列 {columnOffset:F2} · Δ角度 {angleOffset:F2}°" })
            };
            var runtime = new Dictionary<string, object?> { ["image"] = image, ["validRegion"] = valid, ["transform"] = transform };
            context.Log("位置矫正完成；下游 ROI 使用基准图像坐标。");
            image = null; valid = null;
            return ValueTask.FromResult(new NodeExecutionOutput(ports, runtime));
        }
        finally { image?.Dispose(); valid?.Dispose(); }
    }

    public static QuickMatchPose UniquePose(PoseSet poses)
    {
        if (poses.Images.Count != 1 || poses.Images[0].ImageIndex != 1) throw Error("SINGLE_IMAGE_REQUIRED", "基准示教和矫正每轮需要一张图像。");
        var matches = poses.Images[0].Matches;
        if (matches.Count == 0) throw Error("POSE_NOT_FOUND", "未找到匹配目标，本轮不进行矫正。");
        if (matches.Count != 1) throw Error("POSE_AMBIGUOUS", "找到多个目标，请缩小匹配范围，确保目标唯一。");
        ValidatePose(matches[0]); return matches[0];
    }
    public static PositionReference Teach(PoseSet poses)
    {
        var pose = UniquePose(poses);
        var frame = poses.SourceFrame ?? throw Error("FRAME_REQUIRED", "请通过工作流运行后示教。");
        if (frame.Annotated || frame.Count != 1 || frame.ImageIndex != 1 || frame.Width <= 0 || frame.Height <= 0 || string.IsNullOrWhiteSpace(poses.ModelIdentity))
            throw Error("FRAME_INVALID", "示教需要唯一、有效且未绘制标注的输入图像。");
        return new(pose, frame.Width, frame.Height, poses.NodeId, poses.ModelIdentity, frame.ConfigurationIdentity, DateTime.UtcNow, frame.ConfigurationIdentity);
    }
    public static double NormalizeAngle(double degrees) => (degrees % 360 + 540) % 360 - 180;
    private static void ValidatePose(QuickMatchPose pose)
    {
        if (pose is null || !double.IsFinite(pose.Row) || !double.IsFinite(pose.Column) || !double.IsFinite(pose.AngleDegrees) ||
            !double.IsFinite(pose.Score) || pose.Score <= 0 || pose.Score > 1) throw Error("POSE_INVALID", "位姿或评分不是有效数值。");
    }
    private static InvalidDataException Error(string code, string message) => new($"{code}：{message}");
}

public static class PositionTransform
{
    public static HTuple Standard(QuickMatchPose current, QuickMatchPose reference)
    {
        HOperatorSet.VectorAngleToRigid(current.Row, current.Column, current.AngleDegrees * Math.PI / 180,
            reference.Row, reference.Column, reference.AngleDegrees * Math.PI / 180, out HTuple matrix);
        return matrix;
    }
    public static HTuple ForPixels(HTuple standard)
    {
        HOperatorSet.HomMat2dTranslate(standard, .5, .5, out HTuple shifted); using var shiftedScope = shifted;
        HOperatorSet.HomMat2dTranslateLocal(shifted, -.5, -.5, out HTuple adapted); return adapted;
    }
    public static HImage Warp(HImage image, HTuple adapted, int width, int height)
    {
        HOperatorSet.AffineTransImageSize(image, out HObject output, adapted, "bilinear", width, height);
        using (output) return new HImage(output);
    }
    public static HRegion WarpRegion(HRegion region, HTuple adapted, int width, int height)
    {
        HOperatorSet.AffineTransRegion(region, out HObject output, adapted, "nearest_neighbor");
        using (output) return new HRegion(output);
    }
}
