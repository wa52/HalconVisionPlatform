using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

/// <summary>HALCON 24.11 nodes. HALCON objects stay in the in-memory run channel and never enter flow JSON.</summary>
public sealed class HalconNodeProvider : INodeDefinitionProvider
{
    public const string ImageRead = "halcon.image-read";
    public const string CameraCapture = "halcon.camera-capture";
    public const string Threshold = "halcon.threshold";
    public const string BlobAnalysis = "halcon.blob-analysis";
    public const string StraightLineFind = "halcon.straight-line-find";

    public IEnumerable<NodeDefinition> GetDefinitions()
    {
        yield return new NodeDefinition(
            ImageRead, 1, "图像输入", "读取图像", "使用 HALCON 读取单张图像或文件夹中的图像序列，输出运行期 HImage 对象。",
            [new PortDefinition("image", "Image", PortDirection.Output, "halcon.image")],
            [new NodeParameterDefinition("path", "图像文件或文件夹", "path", true, JsonSerializer.SerializeToElement(string.Empty))]);

        yield return new NodeDefinition(
            CameraCapture, 1, "图像输入", "相机采集", "从已连接的 HALCON 采集接口单次抓取一帧图像。",
            [new PortDefinition("image", "Image", PortDirection.Output, "halcon.image")],
            [new NodeParameterDefinition("interface", "相机类别", "cameraInterface", true, JsonSerializer.SerializeToElement("DirectShow")),
             new NodeParameterDefinition("device", "相机设备", "camera", true, JsonSerializer.SerializeToElement("0")),
             new NodeParameterDefinition("grabTimeoutMs", "取图超时（毫秒）", "number", true, JsonSerializer.SerializeToElement(2000))]);

        yield return new NodeDefinition(
            Threshold, 1, "图像处理", "二值化", "对输入 HImage 执行 HALCON Threshold，输出 HRegion。",
            [new PortDefinition("image", "Image", PortDirection.Input, "halcon.image", Required: true),
             new PortDefinition("region", "Region", PortDirection.Output, "halcon.region")],
            [new NodeParameterDefinition("minimum", "灰度下限", "number", true, JsonSerializer.SerializeToElement(128d)),
             new NodeParameterDefinition("maximum", "灰度上限", "number", true, JsonSerializer.SerializeToElement(255d))]);

        yield return new NodeDefinition(
            BlobAnalysis, 1, "特征检测", "Blob 分析", "对二值区域执行 Connection 和面积筛选，输出区域及连通域数量。",
            [new PortDefinition("region", "Region", PortDirection.Input, "halcon.region", Required: true),
             new PortDefinition("regions", "Regions", PortDirection.Output, "halcon.region"),
             new PortDefinition("count", "Count", PortDirection.Output, "number")],
            [new NodeParameterDefinition("minimumArea", "最小面积", "number", true, JsonSerializer.SerializeToElement(1d)),
             new NodeParameterDefinition("maximumArea", "最大面积", "number", true, JsonSerializer.SerializeToElement(1_000_000_000d))]);

        yield return new NodeDefinition(
            StraightLineFind, 1, "特征检测", "直线查找", "在图像上绘制检测范围，设置直线方向与容差；HALCON LinesGauss 输出标注图像、XLD 线轮廓及数量。",
            [new PortDefinition("image", "Image", PortDirection.Input, "halcon.image", Required: true),
             new PortDefinition("resultImage", "ResultImage", PortDirection.Output, "halcon.image"),
             new PortDefinition("lines", "Lines", PortDirection.Output, "halcon.xld"),
             new PortDefinition("count", "Count", PortDirection.Output, "number")],
            [new NodeParameterDefinition("roi", "检测范围", "roiDraw", true, JsonSerializer.SerializeToElement(string.Empty)),
             new NodeParameterDefinition("directionDeg", "直线方向（度，逆时针）", "number", true, JsonSerializer.SerializeToElement(0d)),
             new NodeParameterDefinition("directionToleranceDeg", "方向容差（度）", "number", true, JsonSerializer.SerializeToElement(15d)),
             new NodeParameterDefinition("sigma", "平滑尺度 σ", "number", true, JsonSerializer.SerializeToElement(1.5d)),
             new NodeParameterDefinition("low", "低阈值", "number", true, JsonSerializer.SerializeToElement(3d)),
             new NodeParameterDefinition("high", "高阈值", "number", true, JsonSerializer.SerializeToElement(8d)),
             new NodeParameterDefinition("lightDark", "线条极性 (light/dark)", "string", true, JsonSerializer.SerializeToElement("light"))]);
    }
}

/// <summary>Captures one frame from a camera connected through the workspace camera panel.</summary>
public sealed class HalconCameraCaptureExecutor : INodeExecutor
{
    private readonly HalconCameraManager _cameraManager;

    public HalconCameraCaptureExecutor(HalconCameraManager cameraManager) => _cameraManager = cameraManager;

    public string TypeId => HalconNodeProvider.CameraCapture;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var interfaceId = context.Node.Parameters.TryGetValue("interface", out var interfaceValue)
            ? interfaceValue.GetString()?.Trim()
            : null;
        var device = context.Node.Parameters.TryGetValue("device", out var deviceValue)
            ? deviceValue.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(interfaceId)) interfaceId = "DirectShow";
        if (string.IsNullOrWhiteSpace(device) || string.Equals(device, "default", StringComparison.OrdinalIgnoreCase)) device = "0";

        HImage? image = null;
        try
        {
            image = _cameraManager.GrabFrame(interfaceId, device);
            image.GetImageSize(out int width, out int height);
            using var channels = image.CountChannels();

            var serialized = new Dictionary<string, JsonElement>
            {
                ["image"] = JsonSerializer.SerializeToElement(new
                {
                    source = $"HALCON {interfaceId}",
                    device,
                    count = 1,
                    width,
                    height,
                    channels = channels.I
                })
            };
            var runtime = new Dictionary<string, object?> { ["image"] = image };
            image = null;
            context.Log($"相机单帧采集成功：{device}，{width} × {height}，{channels.I} 通道。");
            return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        catch (HalconException exception)
        {
            throw new InvalidOperationException(
                $"HALCON 相机采集失败（接口：{interfaceId}，设备：{device}）。请检查连接状态、接口配置和设备权限。原始错误：{exception.Message}",
                exception);
        }
        finally
        {
            image?.Dispose();
        }
    }
}

public sealed class HalconImageReadExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.ImageRead;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var path = context.Node.Parameters.TryGetValue("path", out var pathValue) ? pathValue.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请先选择图像文件或文件夹。");
        var fullPath = Path.GetFullPath(path);
        var imagePaths = Directory.Exists(fullPath)
            ? Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedImage)
                .OrderBy(imagePath => imagePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : File.Exists(fullPath) && IsSupportedImage(fullPath) ? [fullPath] : [];
        if (imagePaths.Length == 0)
            throw new FileNotFoundException(Directory.Exists(fullPath) ? "所选文件夹中没有支持的图像文件。" : "图像文件不存在或格式不受支持。", fullPath);

        HImage? image = null;
        var width = 0;
        var height = 0;
        try
        {
            foreach (var imagePath in imagePaths)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var item = new HImage(imagePath);
                if (image is null)
                {
                    item.GetImageSize(out width, out height);
                    image = item.CopyImage();
                }
                else
                {
                    var combined = image.ConcatObj(item);
                    image.Dispose();
                    image = combined;
                }
            }
            context.Log($"HALCON ReadImage：读取 {imagePaths.Length} 张图像（{width} × {height}）。");
            var serialized = new Dictionary<string, JsonElement>
            {
                ["image"] = JsonSerializer.SerializeToElement(new { source = fullPath, paths = imagePaths, count = imagePaths.Length, width, height })
            };
            var runtime = new Dictionary<string, object?> { ["image"] = image };
            image = null;
            return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        finally { image?.Dispose(); }
    }

    private static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".bmp" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".jp2";
}

public sealed class HalconThresholdExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.Threshold;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.RuntimeInputs is null || !context.RuntimeInputs.TryGetValue("image", out var input) || input is not HImage image)
            throw new InvalidDataException("输入端口 Image 未收到 HALCON HImage。请连接“读取图像”节点。");

        var minimum = ReadNumber(context.Node, "minimum");
        var maximum = ReadNumber(context.Node, "maximum");
        if (minimum > maximum) throw new InvalidDataException("灰度下限不能大于灰度上限。");

        using var channelCountTuple = image.CountChannels();
        var channelCount = channelCountTuple.I;
        using var gray = channelCount == 3 ? image.Rgb1ToGray() : null;
        var thresholdInput = gray ?? image;
        if (channelCount != 1 && channelCount != 3)
            throw new InvalidDataException($"二值化仅支持单通道灰度或三通道 RGB 图像，当前通道数：{channelCount}。");

        HRegion? region = thresholdInput.Threshold(minimum, maximum);
        try
        {
        var count = region.CountObj();
        var dimensions = ReadImageDimensions(context, "image");
        context.Log(channelCount == 3
            ? $"HALCON Rgb1ToGray + Threshold({minimum:G}, {maximum:G})：生成 {count} 个区域对象。"
            : $"HALCON Threshold({minimum:G}, {maximum:G})：生成 {count} 个区域对象。");
        var serialized = new Dictionary<string, JsonElement>
        {
            ["region"] = JsonSerializer.SerializeToElement(new { objectCount = count, minimum, maximum, width = dimensions.Width, height = dimensions.Height })
        };
        var runtime = new Dictionary<string, object?> { ["region"] = region };
        region = null;
        return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        finally { region?.Dispose(); }
    }

    private static double ReadNumber(NodeInstance node, string key)
    {
        if (!node.Parameters.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException($"二值化参数 {key} 必须是数字。");
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new InvalidDataException($"二值化参数 {key} 必须是有限数值。");
        return number;
    }

    internal static ImageDimensions ReadImageDimensions(NodeExecutionContext context, string portId)
    {
        if (!context.Inputs.TryGetValue(portId, out var value) || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("width", out var width) || !width.TryGetInt32(out var imageWidth) ||
            !value.TryGetProperty("height", out var height) || !height.TryGetInt32(out var imageHeight))
            throw new InvalidDataException($"输入端口 {portId} 未提供图像尺寸元数据。");
        return new ImageDimensions(imageWidth, imageHeight);
    }
}

public sealed class HalconBlobAnalysisExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.BlobAnalysis;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.RuntimeInputs is null || !context.RuntimeInputs.TryGetValue("region", out var input) || input is not HRegion region)
            throw new InvalidDataException("输入端口 Region 未收到 HALCON HRegion。请连接“二值化”节点。");

        var minimumArea = ReadNumber(context.Node, "minimumArea");
        var maximumArea = ReadNumber(context.Node, "maximumArea");
        if (minimumArea < 0 || maximumArea < minimumArea)
            throw new InvalidDataException("Blob 面积范围无效：面积必须非负，且最大面积不能小于最小面积。");

        using var connected = region.Connection();
        HRegion? filtered = connected.SelectShape("area", "and", minimumArea, maximumArea);
        try
        {
        var count = filtered.CountObj();
        var dimensions = HalconThresholdExecutor.ReadImageDimensions(context, "region");
        context.Log($"HALCON Connection + SelectShape(area)：筛选后 {count} 个连通域。");
        var serialized = new Dictionary<string, JsonElement>
        {
            ["regions"] = JsonSerializer.SerializeToElement(new { count, minimumArea, maximumArea, width = dimensions.Width, height = dimensions.Height }),
            ["count"] = JsonSerializer.SerializeToElement(count)
        };
        var runtime = new Dictionary<string, object?> { ["regions"] = filtered };
        filtered = null;
        return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        finally { filtered?.Dispose(); }
    }

    private static double ReadNumber(NodeInstance node, string key)
    {
        if (!node.Parameters.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException($"Blob 参数 {key} 必须是数字。");
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new InvalidDataException($"Blob 参数 {key} 必须是有限数值。");
        return number;
    }
}

/// <summary>Finds bright/dark line features with HALCON LinesGauss and returns a preview image plus XLD contours.</summary>
public sealed class HalconStraightLineFindExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.StraightLineFind;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.RuntimeInputs is null || !context.RuntimeInputs.TryGetValue("image", out var input) || input is not HImage image)
            throw new InvalidDataException("输入端口 Image 未收到 HALCON HImage。请连接“读取图像”或“相机采集”节点。");

        var sigma = ReadNumber(context.Node, "sigma");
        var low = ReadNumber(context.Node, "low");
        var high = ReadNumber(context.Node, "high");
        var directionDeg = ReadNumber(context.Node, "directionDeg");
        var directionToleranceDeg = ReadNumber(context.Node, "directionToleranceDeg");
        if (sigma <= 0) throw new InvalidDataException("平滑尺度 σ 必须大于 0。");
        if (low < 0 || high <= 0 || low > high)
            throw new InvalidDataException("直线查找阈值无效：低阈值需非负，高阈值需大于 0 且不小于低阈值。");
        if (directionToleranceDeg <= 0 || directionToleranceDeg >= 90)
            throw new InvalidDataException("方向容差必须大于 0 且小于 90 度。");
        var polarity = ReadString(context.Node, "lightDark").Trim().ToLowerInvariant();
        if (polarity is not ("light" or "dark")) throw new InvalidDataException("线条极性只支持 light 或 dark。");

        using var channelCount = image.CountChannels();
        if (channelCount.I is not (1 or 3))
            throw new InvalidDataException($"直线查找仅支持单通道灰度或三通道 RGB 图像，当前通道数：{channelCount.I}。");

        using var gray = channelCount.I == 3 ? image.Rgb1ToGray() : null;
        var searchImage = gray ?? image;
        var dimensions = HalconThresholdExecutor.ReadImageDimensions(context, "image");
        var roi = ReadRectangle(context.Node, dimensions.Width, dimensions.Height);
        using var roiRegion = new HRegion();
        roiRegion.GenRectangle1(roi.Row1, roi.Column1, roi.Row2, roi.Column2);
        using var roiImage = searchImage.ReduceDomain(roiRegion);
        HXLDCont? lines = null;
        HImage? resultImage = null;
        try
        {
            using var foundLines = roiImage.LinesGauss(sigma, low, high, polarity, "true", "bar-shaped", "true");
            var angle = NormalizeRadians(directionDeg * Math.PI / 180d);
            using var forward = SelectAngleBand(foundLines, angle, directionToleranceDeg * Math.PI / 180d);
            using var backward = SelectAngleBand(foundLines, NormalizeRadians(angle + Math.PI), directionToleranceDeg * Math.PI / 180d);
            lines = forward.ConcatObj(backward);
            var count = lines.CountObj();
            using var regions = lines.GenRegionContourXld("margin");
            resultImage = searchImage.PaintRegion(regions, 255d, "margin");
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Log($"HALCON LinesGauss：ROI [{roi.Row1:G},{roi.Column1:G}]-[{roi.Row2:G},{roi.Column2:G}]，方向 {directionDeg:G}° ± {directionToleranceDeg:G}°，查找到 {count} 条线。");
            var serialized = new Dictionary<string, JsonElement>
            {
                ["resultImage"] = JsonSerializer.SerializeToElement(new { count, width = dimensions.Width, height = dimensions.Height }),
                ["lines"] = JsonSerializer.SerializeToElement(new { count, sigma, low, high, lightDark = polarity, width = dimensions.Width, height = dimensions.Height }),
                ["count"] = JsonSerializer.SerializeToElement(count)
            };
            var runtime = new Dictionary<string, object?> { ["resultImage"] = resultImage, ["lines"] = lines };
            resultImage = null;
            lines = null;
            return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        finally
        {
            resultImage?.Dispose();
            lines?.Dispose();
        }
    }

    private static double ReadNumber(NodeInstance node, string key)
    {
        if (!node.Parameters.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble()))
            throw new InvalidDataException($"直线查找参数 {key} 必须是有限数值。");
        return value.GetDouble();
    }

    private static string ReadString(NodeInstance node, string key)
    {
        if (!node.Parameters.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"直线查找参数 {key} 必须是非空字符串。");
        return value.GetString()!;
    }

    private static (double Row1, double Column1, double Row2, double Column2) ReadRectangle(NodeInstance node, int imageWidth, int imageHeight)
    {
        if (!node.Parameters.TryGetValue("roi", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("请先在节点属性中点击“绘制范围”，框选直线查找区域。");
        var parts = value.GetString()!.Split(',');
        if (parts.Length != 4 || !parts.All(part => double.TryParse(part, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var coordinate) && double.IsFinite(coordinate)))
            throw new InvalidDataException("检测范围无效，请重新绘制。");
        var coordinates = parts.Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var row1 = Math.Clamp(Math.Min(coordinates[0], coordinates[2]), 0, imageHeight - 1);
        var column1 = Math.Clamp(Math.Min(coordinates[1], coordinates[3]), 0, imageWidth - 1);
        var row2 = Math.Clamp(Math.Max(coordinates[0], coordinates[2]), 0, imageHeight - 1);
        var column2 = Math.Clamp(Math.Max(coordinates[1], coordinates[3]), 0, imageWidth - 1);
        if (row2 - row1 < 2 || column2 - column1 < 2) throw new InvalidDataException("检测范围太小，请重新绘制。");
        return (row1, column1, row2, column2);
    }

    private static HXLDCont SelectAngleBand(HXLDCont contours, double center, double halfWidth)
    {
        var minimum = center - halfWidth;
        var maximum = center + halfWidth;
        if (minimum < 0)
        {
            using var lower = contours.SelectContoursXld("direction", 0, maximum, 0, 0);
            using var upper = contours.SelectContoursXld("direction", minimum + 2 * Math.PI, 2 * Math.PI, 0, 0);
            return lower.ConcatObj(upper);
        }
        if (maximum >= 2 * Math.PI)
        {
            using var lower = contours.SelectContoursXld("direction", 0, maximum - 2 * Math.PI, 0, 0);
            using var upper = contours.SelectContoursXld("direction", minimum, 2 * Math.PI, 0, 0);
            return lower.ConcatObj(upper);
        }
        return contours.SelectContoursXld("direction", minimum, maximum, 0, 0);
    }

    private static double NormalizeRadians(double value)
    {
        value %= 2 * Math.PI;
        return value < 0 ? value + 2 * Math.PI : value;
    }
}
