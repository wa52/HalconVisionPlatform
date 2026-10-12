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
            StraightLineFind, 1, "特征检测", "直线查找", "沿图像上的参考线布置 HALCON 2D Metrology 测量卡尺，定位边缘并拟合直线。",
            [new PortDefinition("image", "Image", PortDirection.Input, "halcon.image", Required: true),
             new PortDefinition("line", "Line", PortDirection.Output, "halcon.line2d"),
             new PortDefinition("angle", "Angle", PortDirection.Output, "number"),
             new PortDefinition("length", "Length", PortDirection.Output, "number"),
             new PortDefinition("edgePoints", "EdgePoints", PortDirection.Output, "halcon.point-set"),
             new PortDefinition("score", "Score", PortDirection.Output, "number"),
             new PortDefinition("overlay", "Overlay", PortDirection.Output, "halcon.geometry"),
             new PortDefinition("status", "Status", PortDirection.Output, "inspection.result"),
             new PortDefinition("preview", "预览图像", PortDirection.Output, "halcon.image"),
             new PortDefinition("resultImage", "ResultImage", PortDirection.Output, "halcon.image"),
             new PortDefinition("lines", "Lines", PortDirection.Output, "halcon.xld"),
             new PortDefinition("count", "Count", PortDirection.Output, "number")],
            [new NodeParameterDefinition("referenceLine", "参考直线", "lineDraw", true, JsonSerializer.SerializeToElement(string.Empty)),
             new NodeParameterDefinition("numMeasures", "测量卡尺数量", "number", true, JsonSerializer.SerializeToElement(30)),
             new NodeParameterDefinition("measureLength1", "搜索半宽（像素）", "number", true, JsonSerializer.SerializeToElement(20d)),
             new NodeParameterDefinition("measureLength2", "卡尺半长（像素）", "number", true, JsonSerializer.SerializeToElement(5d)),
             new NodeParameterDefinition("measureSigma", "边缘平滑 σ", "number", true, JsonSerializer.SerializeToElement(1d)),
             new NodeParameterDefinition("measureThreshold", "边缘阈值", "number", true, JsonSerializer.SerializeToElement(30d)),
             new NodeParameterDefinition("measureTransition", "边缘极性", "edgeTransition", true, JsonSerializer.SerializeToElement("all")),
             new NodeParameterDefinition("minScore", "最小拟合评分", "number", true, JsonSerializer.SerializeToElement(0.7d)),
             new NodeParameterDefinition("showReferenceLine", "显示参考直线", "bool", true, JsonSerializer.SerializeToElement(true)),
             new NodeParameterDefinition("showMeasureRegions", "显示测量卡尺", "bool", true, JsonSerializer.SerializeToElement(true)),
             new NodeParameterDefinition("showEdgePoints", "显示检测边缘点", "bool", true, JsonSerializer.SerializeToElement(true)),
             new NodeParameterDefinition("showFittedLine", "显示拟合直线", "bool", true, JsonSerializer.SerializeToElement(true))]);
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

/// <summary>Finds a line with HALCON 2D metrology and returns typed geometry, diagnostics, and a rendered overlay.</summary>
public sealed class HalconStraightLineFindExecutor : INodeExecutor
{
    public string TypeId => HalconNodeProvider.StraightLineFind;
    public int Version => 1;

    public ValueTask<NodeExecutionOutput> ExecuteAsync(NodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.RuntimeInputs is null || !context.RuntimeInputs.TryGetValue("image", out var input) || input is not HImage image)
            throw new InvalidDataException("输入端口 Image 未收到 HALCON HImage。请连接“读取图像”或“相机采集”节点。");

        var numMeasures = ReadInteger(context.Node, "numMeasures", 30);
        var hasExplicitReferenceLine = context.Node.Parameters.TryGetValue("referenceLine", out var configuredLine) &&
            configuredLine.ValueKind == JsonValueKind.String && configuredLine.GetString()?.Split(',').Length == 4;
        var usesLegacyRoi = !hasExplicitReferenceLine && context.Node.Parameters.ContainsKey("roi");
        var measureLength1 = ReadNumber(context.Node, "measureLength1", hasExplicitReferenceLine ? 20d : LegacySearchHalfWidth(context.Node));
        var measureLength2 = ReadNumber(context.Node, "measureLength2", 5d);
        var sigma = ReadNumber(context.Node, "measureSigma", 1d);
        var threshold = ReadNumber(context.Node, "measureThreshold", usesLegacyRoi ? 5d : 30d);
        var minScore = ReadNumber(context.Node, "minScore", usesLegacyRoi ? 0.2d : 0.7d);
        var transition = ReadString(context.Node, "measureTransition", "all").Trim().ToLowerInvariant();
        var showReference = ReadBoolean(context.Node, "showReferenceLine", true);
        var showMeasures = ReadBoolean(context.Node, "showMeasureRegions", true);
        var showEdges = ReadBoolean(context.Node, "showEdgePoints", true);
        var showFit = ReadBoolean(context.Node, "showFittedLine", true);
        if (numMeasures is < 2 or > 1000) throw new InvalidDataException("测量卡尺数量必须在 2–1000 之间。");
        if (measureLength1 <= 0 || measureLength2 <= 0) throw new InvalidDataException("搜索半宽和卡尺半长必须大于 0。");
        if (sigma <= 0 || threshold <= 0) throw new InvalidDataException("边缘平滑 σ 和边缘阈值必须大于 0。");
        if (minScore is < 0 or > 1) throw new InvalidDataException("最小拟合评分必须在 0–1 之间。");
        if (transition is not ("all" or "positive" or "negative"))
            throw new InvalidDataException("边缘极性仅支持 all、positive 或 negative。");

        using var channelCount = image.CountChannels();
        if (channelCount.I is not (1 or 3))
            throw new InvalidDataException($"直线查找仅支持单通道灰度或三通道 RGB 图像，当前通道数：{channelCount.I}。");

        using var gray = channelCount.I == 3 ? image.Rgb1ToGray() : null;
        var searchImage = gray ?? image;
        var dimensions = HalconThresholdExecutor.ReadImageDimensions(context, "image");
        var reference = ReadReferenceLine(context.Node, dimensions.Width, dimensions.Height);
        var geometryMeasures = CreateMeasureRegionPolygons(reference, numMeasures, measureLength1, measureLength2);
        var edgePoints = new PointSet([]);
        Line2D? fittedLine = null;
        double? score = null;
        HXLDCont? fitContour = null;
        HImage? preview = null;
        var status = new LineFindStatus(LineFindStatus.NotFound, "未找到满足参数的直线。");
        try
        {
            using var model = new HMetrologyModel();
            model.SetMetrologyModelImageSize(dimensions.Width, dimensions.Height);
            using var emptyParameterNames = new HTuple();
            using var emptyParameterValues = new HTuple();
            var index = model.AddMetrologyObjectLineMeasure(reference.RowBegin, reference.ColumnBegin,
                reference.RowEnd, reference.ColumnEnd, measureLength1, measureLength2, sigma, threshold,
                emptyParameterNames, emptyParameterValues);
            using var objectIndex = new HTuple(index);
            using var parameterNames = new HTuple(new[] { "num_measures", "measure_transition", "measure_select", "min_score" });
            using var parameterValues = new HTuple(numMeasures).TupleConcat(transition).TupleConcat("all").TupleConcat(minScore);
            model.SetMetrologyObjectParam(objectIndex, parameterNames, parameterValues);
            context.Log("2D Metrology：测量对象已配置，开始拟合。");
            model.ApplyMetrologyModel(searchImage);
            context.Log("2D Metrology：拟合完成，读取结果。");

            var instanceCount = model.GetMetrologyObjectNumInstances(index);
            if (instanceCount > 0)
            {
                fittedLine = new Line2D(FirstResult(model, index, "row_begin"), FirstResult(model, index, "column_begin"),
                    FirstResult(model, index, "row_end"), FirstResult(model, index, "column_end"));
                using var scoreValues = model.GetMetrologyObjectResult(index, "all", new HTuple("result_type"), new HTuple("score"));
                score = scoreValues.Length > 0 ? scoreValues[0].D : 0d;
                using var usedRows = model.GetMetrologyObjectResult(index, "all", new HTuple("used_edges"), new HTuple("row"));
                using var usedColumns = model.GetMetrologyObjectResult(index, "all", new HTuple("used_edges"), new HTuple("column"));
                edgePoints = new PointSet(Enumerable.Range(0, Math.Min(usedRows.Length, usedColumns.Length))
                    .Select(pointIndex => new Point2D(usedRows[pointIndex].D, usedColumns[pointIndex].D)).ToArray());
                fitContour = model.GetMetrologyObjectResultContour(index, "all", 1.5);
                status = new LineFindStatus(LineFindStatus.Found, $"已找到直线，使用 {edgePoints.Points.Count}/{numMeasures} 个测量卡尺。");
            }

            using var measures = model.GetMetrologyObjectMeasures(objectIndex, transition, out var measureRows, out var measureColumns);
            measureRows.Dispose();
            measureColumns.Dispose();
            context.Log("2D Metrology：读取卡尺轮廓完成，开始绘制结果。");
            using var referenceContour = CreateLineContour(reference);
            using var edgeContour = CreatePointCrosses(edgePoints, 5d);
            preview = RenderPreview(searchImage, referenceContour, measures, edgeContour, fitContour,
                showReference, showMeasures, showEdges, showFit);
            context.CancellationToken.ThrowIfCancellationRequested();
            var count = fittedLine is null ? 0 : 1;
            context.Log($"HALCON 2D Metrology：卡尺 {numMeasures}，阈值 {threshold:G}，状态 {status.Code}，评分 {score?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "—"}。");
            var edgePointOutput = JsonSerializer.SerializeToElement(edgePoints);
            var overlay = new GeometryOverlay(reference, fittedLine, geometryMeasures, edgePoints);
            var serialized = new Dictionary<string, JsonElement>
            {
                ["line"] = SerializeNullable(fittedLine),
                ["angle"] = SerializeNullable(fittedLine?.AngleDegrees),
                ["length"] = SerializeNullable(fittedLine?.Length),
                ["edgePoints"] = edgePointOutput,
                ["score"] = SerializeNullable(score),
                ["overlay"] = JsonSerializer.SerializeToElement(overlay),
                ["status"] = JsonSerializer.SerializeToElement(status),
                ["preview"] = JsonSerializer.SerializeToElement(new { count, width = dimensions.Width, height = dimensions.Height, channels = 3 }),
                ["resultImage"] = JsonSerializer.SerializeToElement(new { count, width = dimensions.Width, height = dimensions.Height, channels = 3 }),
                ["lines"] = JsonSerializer.SerializeToElement(new { count, width = dimensions.Width, height = dimensions.Height }),
                ["count"] = JsonSerializer.SerializeToElement(count)
            };
            var runtime = new Dictionary<string, object?>
            {
                ["line"] = fittedLine, ["angle"] = fittedLine?.AngleDegrees, ["length"] = fittedLine?.Length,
                ["edgePoints"] = edgePoints, ["score"] = score, ["overlay"] = overlay, ["status"] = status,
                ["preview"] = preview, ["resultImage"] = preview, ["lines"] = fitContour, ["count"] = count
            };
            preview = null;
            fitContour = null;
            return ValueTask.FromResult(new NodeExecutionOutput(serialized, runtime));
        }
        finally
        {
            preview?.Dispose();
            fitContour?.Dispose();
        }
    }

    private static double ReadNumber(NodeInstance node, string key, double fallback)
    {
        if (!node.Parameters.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble()))
            throw new InvalidDataException($"直线查找参数 {key} 必须是有限数值。");
        return value.GetDouble();
    }

    private static int ReadInteger(NodeInstance node, string key, int fallback)
    {
        var number = ReadNumber(node, key, fallback);
        if (number != Math.Truncate(number)) throw new InvalidDataException($"直线查找参数 {key} 必须是整数。");
        return checked((int)number);
    }

    private static string ReadString(NodeInstance node, string key, string fallback)
    {
        if (!node.Parameters.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"直线查找参数 {key} 不能为空。");
        return value.GetString()!;
    }

    private static bool ReadBoolean(NodeInstance node, string key, bool fallback)
    {
        if (!node.Parameters.TryGetValue(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"直线查找参数 {key} 必须是布尔值。")
        };
    }

    private static Line2D ReadReferenceLine(NodeInstance node, int imageWidth, int imageHeight)
    {
        var raw = node.Parameters.TryGetValue("referenceLine", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var parts = raw?.Split(',');
        if (parts is not { Length: 4 } && node.Parameters.TryGetValue("roi", out var oldValue) && oldValue.ValueKind == JsonValueKind.String)
        {
            var oldParts = oldValue.GetString()?.Split(',');
            if (oldParts is { Length: 4 })
            {
                var old = ParseCoordinates(oldParts, "旧版检测范围");
                var rowSpan = Math.Abs(old[2] - old[0]);
                var columnSpan = Math.Abs(old[3] - old[1]);
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                parts = rowSpan >= columnSpan
                    ? [old[0].ToString("R", invariant), ((old[1] + old[3]) / 2d).ToString("R", invariant), old[2].ToString("R", invariant), ((old[1] + old[3]) / 2d).ToString("R", invariant)]
                    : [((old[0] + old[2]) / 2d).ToString("R", invariant), old[1].ToString("R", invariant), ((old[0] + old[2]) / 2d).ToString("R", invariant), old[3].ToString("R", invariant)];
            }
        }
        if (parts is not { Length: 4 }) throw new InvalidDataException("请点击“绘制参考线”，沿要检测的直边拖动鼠标。");
        var coordinates = ParseCoordinates(parts, "参考直线");
        var line = new Line2D(Math.Clamp(coordinates[0], 0, imageHeight - 1), Math.Clamp(coordinates[1], 0, imageWidth - 1),
            Math.Clamp(coordinates[2], 0, imageHeight - 1), Math.Clamp(coordinates[3], 0, imageWidth - 1));
        if (line.Length < 3) throw new InvalidDataException("参考直线长度至少为 3 像素，请重新绘制。");
        return line;
    }

    private static double LegacySearchHalfWidth(NodeInstance node)
    {
        if (!node.Parameters.TryGetValue("roi", out var value) || value.ValueKind != JsonValueKind.String) return 20d;
        var parts = value.GetString()?.Split(',');
        if (parts is not { Length: 4 }) return 20d;
        var coordinates = ParseCoordinates(parts, "旧版检测范围");
        var rowSpan = Math.Abs(coordinates[2] - coordinates[0]);
        var columnSpan = Math.Abs(coordinates[3] - coordinates[1]);
        var perpendicularSpan = rowSpan >= columnSpan ? columnSpan : rowSpan;
        return Math.Clamp(perpendicularSpan / 2d, 1d, 500d);
    }

    private static double[] ParseCoordinates(string[] parts, string label)
    {
        if (parts.Length != 4 || !parts.All(part => double.TryParse(part, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var coordinate) && double.IsFinite(coordinate)))
            throw new InvalidDataException($"{label}坐标无效，请重新绘制。");
        return parts.Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static double FirstResult(HMetrologyModel model, int index, string parameter)
    {
        using var name = new HTuple("result_type");
        using var value = new HTuple(parameter);
        using var result = model.GetMetrologyObjectResult(index, "all", name, value);
        if (result.Length == 0) throw new InvalidDataException($"HALCON 没有返回拟合参数 {parameter}。");
        return result[0].D;
    }

    private static JsonElement SerializeNullable<T>(T? value) => value is null
        ? JsonSerializer.SerializeToElement<object?>(null)
        : JsonSerializer.SerializeToElement(value);

    private static HXLDCont CreateLineContour(Line2D line)
    {
        var contour = new HXLDCont();
        using var rows = new HTuple(new[] { line.RowBegin, line.RowEnd });
        using var columns = new HTuple(new[] { line.ColumnBegin, line.ColumnEnd });
        contour.GenContourPolygonXld(rows, columns);
        return contour;
    }

    private static HXLDCont CreatePointCrosses(PointSet points, double size)
    {
        var contours = new HXLDCont();
        if (points.Points.Count == 0) return contours;
        using var rows = new HTuple(points.Points.Select(point => point.Row).ToArray());
        using var columns = new HTuple(points.Points.Select(point => point.Column).ToArray());
        contours.GenCrossContourXld(rows, columns, new HTuple(size), Math.PI / 4);
        return contours;
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> CreateMeasureRegionPolygons(Line2D line, int count, double normalHalfLength, double tangentHalfLength)
    {
        var dRow = line.RowEnd - line.RowBegin;
        var dColumn = line.ColumnEnd - line.ColumnBegin;
        var length = Math.Sqrt(dRow * dRow + dColumn * dColumn);
        var tangentRow = dRow / length; var tangentColumn = dColumn / length;
        var normalRow = -tangentColumn; var normalColumn = tangentRow;
        var result = new List<IReadOnlyList<Point2D>>(count);
        for (var index = 0; index < count; index++)
        {
            var fraction = index / (double)(count - 1);
            var centerRow = line.RowBegin + dRow * fraction;
            var centerColumn = line.ColumnBegin + dColumn * fraction;
            Point2D At(int normalSign, int tangentSign) => new(
                centerRow + normalRow * normalHalfLength * normalSign + tangentRow * tangentHalfLength * tangentSign,
                centerColumn + normalColumn * normalHalfLength * normalSign + tangentColumn * tangentHalfLength * tangentSign);
            result.Add([At(-1, -1), At(-1, 1), At(1, 1), At(1, -1), At(-1, -1)]);
        }
        return result;
    }

    private static HImage RenderPreview(HImage gray, HXLDCont reference, HXLDCont measures, HXLDCont edges, HXLDCont? fitted,
        bool showReference, bool showMeasures, bool showEdges, bool showFit)
    {
        HImage? red = gray.CopyImage(); HImage? green = gray.CopyImage(); HImage? blue = gray.CopyImage();
        try
        {
            if (showReference)
            {
                red = PaintChannel(red, reference, 255d);
                green = PaintChannel(green, reference, 220d);
                blue = PaintChannel(blue, reference, 0d);
            }
            if (showMeasures)
            {
                red = PaintChannel(red, measures, 0d);
                green = PaintChannel(green, measures, 80d);
                blue = PaintChannel(blue, measures, 255d);
            }
            if (showEdges)
            {
                red = PaintChannel(red, edges, 0d);
                green = PaintChannel(green, edges, 255d);
                blue = PaintChannel(blue, edges, 0d);
            }
            if (showFit && fitted is not null)
            {
                red = PaintChannel(red, fitted, 255d);
                green = PaintChannel(green, fitted, 0d);
                blue = PaintChannel(blue, fitted, 0d);
            }
            return red.Compose3(green, blue);
        }
        finally { red?.Dispose(); green?.Dispose(); blue?.Dispose(); }
    }

    private static HImage PaintChannel(HImage channel, HXLDCont contours, double value)
    {
        if (!contours.IsInitialized() || contours.CountObj() == 0) return channel;
        using var regions = contours.GenRegionContourXld("margin");
        var painted = channel.PaintRegion(regions, value, "margin");
        channel.Dispose();
        return painted;
    }
}
