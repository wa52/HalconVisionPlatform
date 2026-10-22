using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

if (args.Length < 2) { Console.Error.WriteLine("用法：DlRoiChecks <本机组合方案.hvproj> <输出目录> [重复轮数]"); return 2; }
var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
var checks = new List<string>(); var timings = new List<object>();
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
try
{
    if (args.Contains("--ui-contracts")) { UiContracts.CheckRoiLayer(Path.GetFullPath(args[0])); Console.WriteLine("PASS ROI 叠加层布局契约"); return 0; }
    if (args.Contains("--dashboard-contract")) { UiContracts.CheckDashboardActivation(Path.GetFullPath(args[0]), output); Console.WriteLine("PASS 运行后显示检测面板"); return 0; }
    var store = new ProjectStore(); var original = store.OpenProject(Path.GetFullPath(args[0]));
    var flow = original.Project.Flows.Single();
    var reader = flow.Nodes.Single(node => node.TypeId == HalconNodeProvider.ImageRead);
    var imagePath = reader.Parameters["path"].GetString()!;
    using var image = new HImage(imagePath); image.GetImageSize(out int width, out int height);
    var rois = new[] {
        new DetectionRoi(Guid.NewGuid(), "区域 A", height * .15, width * .1, height * .8, width * .6),
        new DetectionRoi(Guid.NewGuid(), "区域 B（反向越界）", height * .8, width * .6, -4, -8),
        new DetectionRoi(Guid.NewGuid(), "禁用区域", 0, 0, height - 1, width - 1, false)
    };
    var nodes = flow.Nodes.Select(node => node.Id == reader.Id ? node : WithRois(node, rois)).ToList();
    flow = flow with { Nodes = nodes };
    var fixture = Path.Combine(output, "多ROI验收.hvproj");
    var project = original.Project with { Id = Guid.NewGuid(), Name = "多 ROI 验收", Flows = [flow] };
    store.SaveProject(new(output, project, fixture), [flow]);
    var reloaded = store.OpenProject(fixture).Project.Flows.Single();
    Check(reloaded.Nodes.Skip(1).All(node => DetectionRoi.Parse(node.Parameters["rois"]).SequenceEqual(rois)), "保存恢复 ROI 的 ID、名称、范围和禁用状态");
    var registry = BuiltInNodeCatalog.Create();
    using var camera = new HalconCameraManager();
    using var cache = new DlModelSessionCache(); using var shapes = new ShapeMatchModelCache(); var cached = args.Contains("--cache");
    registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(camera),
        new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor(), new HalconStraightLineFindExecutor(),
        new HalconSemanticSegmentationExecutor(cached ? cache : null), new HalconClassificationExecutor(cached ? cache : null), new HalconObjectDetectionExecutor(cached ? cache : null),
        new HalconQuickMatchExecutor(shapes), new HalconPositionCorrectionExecutor()]);
    var validator = new WorkflowGraphValidator(registry); var engine = new WorkflowExecutionEngine(registry, validator);
    Check(validator.Validate(flow).Count == 0, "真实组合流程端口和参数校验");
    var vmFlow = new FlowTabViewModel(flow, registry, validator);
    var parameter = vmFlow.Nodes.First(node => node.Id != reader.Id).Parameters.Single(item => item.IsRoiList);
    parameter.SaveDrawnRoi(rois[0].Id, 10, 20, 40, 50);
    Check(parameter.ReadRois()[0].Id == rois[0].Id && parameter.ReadRois()[0].Name == rois[0].Name, "重绘保留 ROI 身份和名称");
    parameter.SetRois(rois); parameter.SaveDrawnRoi(null, 10, 10, 50, 50);
    Check(parameter.ReadRois().Count == 4 && parameter.ReadRois()[3].Id != Guid.Empty, "新增 ROI 生成唯一标识");
    parameter.SetRois(rois); parameter.ValueText = JsonSerializer.Serialize(new[] { rois[0], rois[1] with { Name = rois[0].Name } }); parameter.Commit();
    bool blocked = false; try { vmFlow.EnsureRoiEditsValid(); } catch (InvalidDataException) { blocked = true; }
    Check(blocked, "无效名称编辑阻止执行而非使用旧值"); parameter.SetRois(rois);
    var duplicate = flow with { Nodes = nodes.Select(node => node.Id == reader.Id ? node : WithRois(node, [rois[0], rois[0]])).ToList() };
    Check(validator.Validate(duplicate).Count > 0, "重复 ROI ID 被流程校验拒绝");
    var rounds = args.Length > 2 ? int.Parse(args[2]) : 3;
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, JsonElement>>? firstOutputs = null;
    for (var round = 0; round < rounds; round++)
    {
        var watch = Stopwatch.StartNew(); Console.WriteLine($"BEGIN multi round {round + 1}");
        using var run = await engine.RunAsync(flow);
        RequireSuccess(run);
        var resultMap = run.NodeResults.ToDictionary(result => result.NodeId, result => result.Outputs);
        firstOutputs ??= resultMap;
        foreach (var node in nodes.Where(node => node.Id != reader.Id))
        {
            var result = resultMap[node.Id]; var perRoi = result["roiResults"].Deserialize<List<JsonElement>>()!;
            if (cached) Check(result["modelInfo"].GetProperty("cacheHit").GetBoolean() == (round > 0), "缓存首次加载、后续命中");
            Check(perRoi.Count == 2 && perRoi.Select(item => item.GetProperty("RoiId").GetGuid()).SequenceEqual(rois.Take(2).Select(roi => roi.Id)), $"{node.DisplayName}：启用顺序及独立结果（轮 {round + 1}）");
            Check(result["roi"].ValueKind == JsonValueKind.Null, "多 ROI 不伪造单范围输出");
            var runtime = run.RuntimeOutputs![node.Id];
            if (node.TypeId == HalconNodeProvider.Classification)
            {
                Check(result["classId"].ValueKind == JsonValueKind.Null && result["confidence"].ValueKind == JsonValueKind.Null, "分类多 ROI 标量为空");
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(node.Parameters["modelPackage"].GetString()!, "manifest.json")));
                var names = manifest.RootElement.GetProperty("classNames").EnumerateArray().Select(item => item.GetString()).ToArray();
                foreach (var item in perRoi)
                    Check(names[item.GetProperty("ClassId").GetInt32()] == item.GetProperty("ClassName").GetString() && item.GetProperty("Scores").GetArrayLength() == names.Length, "实际分类结果与训练类别映射及全部评分对应");
            }
            else
            {
                var masks = (HImage)runtime["mask"]!; Check(masks.CountObj() == 2, "独立掩膜没有被重叠 ROI 覆盖");
                for (int i = 0; i < 2; i++)
                {
                    var bounds = Bounds(rois[i], width, height);
                    using var mask = masks.SelectObj(i + 1); mask.GetImageSize(out int w, out int h);
                    using var domain = mask.GetDomain(); domain.SmallestRectangle1(out int r1, out int c1, out int r2, out int c2);
                    Check(w == width && h == height && bounds == new Rectangle2D(r1, c1, r2, c2), "掩膜原图尺寸及有效域坐标精确一致");
                    if (round == 0) mask.WriteImage("png", 0, Path.Combine(output, $"roi-{i + 1}-mask.png"));
                }
                var objects = result["objects"].Deserialize<List<SegmentationObject>>()!;
                Check(objects.All(item => rois.Take(2).Any(roi => roi.Id == item.RoiId && roi.Name == item.RoiName)), "分割对象携带所属 ROI 标识");
            }
            if (round == 0) ((HImage)runtime["preview"]!).WriteImage("png", 0, Path.Combine(output, node.TypeId + "-preview.png"));
        }
        var native = run.RuntimeOutputs!.Values.SelectMany(ports => ports.Values).OfType<HObject>().ToArray();
        run.Dispose(); Check(native.All(value => !value.IsInitialized()), "本轮原生输出全部释放");
        using var process = Process.GetCurrentProcess(); process.Refresh();
        timings.Add(new { round = round + 1, wallMs = watch.Elapsed.TotalMilliseconds, privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64 });
        File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new { checks, timings }, new JsonSerializerOptions { WriteIndented = true }));
    }
    if (cached)
    {
        Check(cache.Count == 2, "缓存容量及两个模型会话归属"); cache.Clear(); Check(cache.Count == 0, "清空缓存释放全部模型条目");
        using var reloadedRun = await engine.RunAsync(flow); RequireSuccess(reloadedRun);
        Check(reloadedRun.NodeResults.Where(item => item.NodeId != reader.Id).All(item => !item.Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean()), "清理后不复用旧会话");
    }
    // Differential reference: physically crop the source on disk and infer each crop as a full image.
    for (int i = 0; i < 2; i++)
    {
        var bounds = Bounds(rois[i], width, height); var path = Path.Combine(output, $"reference-crop-{i + 1}.png");
        using (var crop = image.CropRectangle1(bounds.RowBegin, bounds.ColumnBegin, bounds.RowEnd, bounds.ColumnEnd)) crop.WriteImage("png", 0, path);
        var croppedReader = reader with { Parameters = new(reader.Parameters) { ["path"] = JsonSerializer.SerializeToElement(path) } };
        var single = flow with { Nodes = nodes.Select(node => node.Id == reader.Id ? croppedReader : WithRois(node, [])).ToList() };
        Console.WriteLine($"BEGIN independent physical crop {i + 1}");
        using var reference = await engine.RunAsync(single); RequireSuccess(reference);
        foreach (var item in reference.NodeResults.Where(item => item.NodeId != reader.Id))
        {
            var expected = firstOutputs![item.NodeId]["roiResults"][i]; var actual = item.Outputs["roiResults"][0];
            if (item.Outputs.ContainsKey("classId"))
                Check(expected.GetProperty("ClassId").GetInt32() == actual.GetProperty("ClassId").GetInt32() &&
                    Math.Abs(expected.GetProperty("Confidence").GetDouble() - actual.GetProperty("Confidence").GetDouble()) < .0001, "分类多 ROI 与独立真实裁剪输入一致");
            else
                Check(expected.GetProperty("Count").GetInt32() == actual.GetProperty("Count").GetInt32() && expected.GetProperty("Area").GetDouble() == actual.GetProperty("Area").GetDouble(), "分割多 ROI 与独立真实裁剪区域数量/面积一致");
        }
    }
    foreach (var bad in new[] { new[] { rois[0] with { Enabled = false } }, new[] { rois[0] with { RowBegin = height + 5, RowEnd = height + 10 } } })
    {
        using var failed = await engine.RunAsync(flow with { Nodes = nodes.Select(node => node.Id == reader.Id ? node : WithRois(node, bad)).ToList() });
        Check(failed.Status == WorkflowRunStatus.Failed && failed.NodeResults.Count(result => result.Status == NodeExecutionStatus.Failed) == 2, "禁用全部或完全越界明确失败");
        Check(failed.RuntimeOutputs!.Keys.All(id => id == reader.Id), "失败节点不泄漏半份输出");
    }
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel(); using var cancelled = await engine.RunAsync(flow, cancellationToken: cancellation.Token);
        Check(cancelled.Status == WorkflowRunStatus.Cancelled && (cancelled.RuntimeOutputs?.Count ?? 0) == 0, "执行前取消没有产生原生输出");
    }
    if (cached)
    {
        var node = nodes.Single(item => item.TypeId == HalconNodeProvider.Classification);
        var executor = new HalconClassificationExecutor(cache);
        var normal = new NodeExecutionContext(node, new Dictionary<string, JsonElement>(), CancellationToken.None, _ => { }, new Dictionary<string, object?> { ["image"] = image });
        var switched = node with { Parameters = new(node.Parameters) { ["deviceIndex"] = JsonSerializer.SerializeToElement(99999) } };
        bool badDevice = false;
        try { await executor.ExecuteAsync(normal with { Node = switched }); } catch (InvalidOperationException) { badDevice = true; }
        Check(badDevice && cache.Count == 1, "错误设备不回退；满容量加载失败后仍保留最近有效分类会话");
        using (var fresh = Wrap(await executor.ExecuteAsync(normal)))
            Check(fresh.NodeResults[0].Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean(), "恢复原设备后复用同模型身份");
        using (var other = Wrap(await new HalconSemanticSegmentationExecutor(cache).ExecuteAsync(normal with { Node = nodes.Single(item => item.TypeId == HalconNodeProvider.SemanticSegmentation) })))
            Check(cache.Count == 2 && !other.NodeResults[0].Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean(), "被容量淘汰的分割会话重建供取消隔离检查");
        using var cancellation = new CancellationTokenSource();
        var context = new NodeExecutionContext(node, new Dictionary<string, JsonElement>(), cancellation.Token,
            log => { if (log.StartsWith(rois[0].Name + "：")) cancellation.Cancel(); }, new Dictionary<string, object?> { ["image"] = image });
        bool interrupted = false;
        try { await executor.ExecuteAsync(context); } catch (OperationCanceledException) { interrupted = true; }
        Check(interrupted && cache.Count == 1 && image.IsInitialized(), "首个 ROI 后取消：会话移出缓存且输入图像仍有效");
        var wrongModel = node with { Parameters = new(node.Parameters) { ["modelPackage"] = nodes.Single(item => item.TypeId == HalconNodeProvider.SemanticSegmentation).Parameters["modelPackage"] } };
        bool rejected = false;
        try { await executor.ExecuteAsync(context with { Node = wrongModel, CancellationToken = CancellationToken.None, Log = _ => { } }); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "模型任务切换不复用错误类型会话");
        using (var small = new DlModelSessionCache(1))
        {
            using var a = Wrap(await new HalconClassificationExecutor(small).ExecuteAsync(normal));
            var another = normal with { Node = node with { Id = Guid.NewGuid() } };
            using var b = Wrap(await new HalconClassificationExecutor(small).ExecuteAsync(another));
            Check(small.Count == 1 && b.NodeResults[0].Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean() && small.ModelLoads == 1, "同模型不同节点共用容量项");
            var segmentationNode = nodes.Single(item => item.TypeId == HalconNodeProvider.SemanticSegmentation);
            using var c = Wrap(await new HalconSemanticSegmentationExecutor(small).ExecuteAsync(normal with { Node = segmentationNode }));
            Check(small.Count == 1 && !c.NodeResults[0].Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean() && small.ModelLoads == 2, "容量满时不同模型身份淘汰旧会话");
            using var d = Wrap(await new HalconClassificationExecutor(small).ExecuteAsync(normal));
            Check(!d.NodeResults[0].Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean() && small.ModelLoads == 3, "被淘汰的分类模型及预处理过程可以重建");
        }
        cache.Dispose(); Check(cache.Count == 0, "关闭缓存释放全部条目");
        bool disposed = false;
        try { await executor.ExecuteAsync(context with { CancellationToken = CancellationToken.None, Log = _ => { } }); }
        catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "关闭后拒绝创建或借用会话");
    }
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { status = "PASS", checks, timings, outputs = firstOutputs }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS {checks.Count} assertions"); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); return 1; }

static NodeInstance WithRois(NodeInstance node, IEnumerable<DetectionRoi> rois) => node with { Parameters = new(node.Parameters) { ["rois"] = JsonSerializer.SerializeToElement(rois) } };
static Rectangle2D Bounds(DetectionRoi roi, int w, int h) => new(Math.Floor(Math.Clamp(Math.Min(roi.RowBegin, roi.RowEnd), 0, h - 1)), Math.Floor(Math.Clamp(Math.Min(roi.ColumnBegin, roi.ColumnEnd), 0, w - 1)), Math.Ceiling(Math.Clamp(Math.Max(roi.RowBegin, roi.RowEnd), 0, h - 1)), Math.Ceiling(Math.Clamp(Math.Max(roi.ColumnBegin, roi.ColumnEnd), 0, w - 1)));
static void RequireSuccess(WorkflowExecutionRun run) { if (run.Status != WorkflowRunStatus.Success) throw new InvalidOperationException(string.Join("\n", run.NodeResults.Select(result => result.Error).Where(value => value is not null))); }
static WorkflowExecutionRun Wrap(NodeExecutionOutput output) => new(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow,
    WorkflowRunStatus.Success, [new(Guid.Empty, "独立执行", NodeExecutionStatus.Success, DateTime.UtcNow, DateTime.UtcNow, 0, output.Ports, null)], [], [],
    new Dictionary<Guid, IReadOnlyDictionary<string, object?>> { [Guid.Empty] = output.RuntimePorts ?? new Dictionary<string, object?>() });
