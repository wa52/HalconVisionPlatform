using System.IO;
using System.Text.Json;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Passed = [];
    private static readonly List<long> MemorySamples = [];
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); _checks++; Passed.Add(label); }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("用法：<已生成快捷匹配检查目录> <新的输出目录> [P1-P6检测项目]"); return 2; }
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("拒绝覆盖已有检查目录。");
        Directory.CreateDirectory(output);
        try
        {
            var templatePath = Path.Combine(Path.GetFullPath(args[0]), "template.png");
            var rotatedPath = Path.Combine(Path.GetFullPath(args[0]), "input", "01-rotated.png");
            var blankPath = Path.Combine(Path.GetFullPath(args[0]), "input", "02-empty.png");
            using var models = new ShapeMatchModelCache(); using var dl = new DlModelSessionCache(1); using var cameras = new HalconCameraManager();
            var registry = BuiltInNodeCatalog.Create();
            registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(cameras), new HalconThresholdExecutor(),
                new HalconBlobAnalysisExecutor(), new HalconStraightLineFindExecutor(), new HalconSemanticSegmentationExecutor(dl),
                new HalconClassificationExecutor(dl), new HalconObjectDetectionExecutor(dl), new HalconQuickMatchExecutor(models), new HalconPositionCorrectionExecutor()]);
            var validator = new WorkflowGraphValidator(registry); var engine = new WorkflowExecutionEngine(registry, validator);
            NodeInstance Node(string type, double x, double y) => new() { TypeId = type, DisplayName = registry.Definitions.Single(d => d.TypeId == type).DisplayName, X = x, Y = y,
                Parameters = registry.Definitions.Single(d => d.TypeId == type).Parameters.Where(p => p.DefaultValue.HasValue).ToDictionary(p => p.Id, p => p.DefaultValue!.Value) };
            NodeInstance With(NodeInstance node, string key, object? value) => node with { Parameters = new(node.Parameters) { [key] = JsonSerializer.SerializeToElement(value) } };
            var reader = With(Node(HalconNodeProvider.ImageRead, 60, 100), "path", templatePath);
            var matcher = With(With(Node(HalconNodeProvider.QuickMatch, 310, 100), "templateImage", templatePath), "templateRoi", "90,100,215,225");
            var correction = Node(HalconNodeProvider.PositionCorrection, 570, 100);
            var threshold = Node(HalconNodeProvider.Threshold, 810, 100);
            var flow = new FlowDocument { Name = "位置矫正检查", Nodes = [reader, matcher, correction, threshold],
                Edges = [new(Guid.NewGuid(), reader.Id, "image", matcher.Id, "image"), new(Guid.NewGuid(), reader.Id, "image", correction.Id, "image"),
                    new(Guid.NewGuid(), matcher.Id, "poses", correction.Id, "poses"), new(Guid.NewGuid(), correction.Id, "image", threshold.Id, "image")],
                DetectionPanels = [new(Guid.NewGuid(), correction.Id, Contents: [new(Guid.NewGuid(), correction.Id, Overlays: [new(threshold.Id)])])] };
            WorkflowExecutionRun Run(string path, FlowDocument? configuration = null)
            {
                var prepared = flow with { Nodes = flow.Nodes.Select(n => n.Id == reader.Id ? With(n, "path", path) : n).ToList() };
                return engine.RunAsync(prepared, configurationFlow: configuration ?? flow).GetAwaiter().GetResult();
            }
            NodeExecutionResult Result(WorkflowExecutionRun run, Guid id) => run.NodeResults.Single(r => r.NodeId == id);
            void DisposeRun(WorkflowExecutionRun run)
            {
                var objects = run.RuntimeOutputs?.Values.SelectMany(v => v.Values).OfType<HObject>().Distinct(ReferenceEqualityComparer.Instance).Cast<HObject>().ToArray() ?? [];
                run.Dispose(); Check(objects.All(o => !o.IsInitialized()), "本轮输出已释放");
            }
            void Fails(WorkflowExecutionRun run, string code)
            {
                Check(Result(run, correction.Id).Status == NodeExecutionStatus.Failed && Result(run, correction.Id).Error!.Contains(code), code);
                Check(Result(run, threshold.Id).Status == NodeExecutionStatus.Skipped && !run.RuntimeOutputs!.ContainsKey(correction.Id), "失败阻断且无旧图像"); DisposeRun(run);
            }
            Check(validator.Validate(flow).Count == 0, "双输入输出同名端口可校验");
            var initial = Run(templatePath);
            Check(Result(initial, matcher.Id).Status == NodeExecutionStatus.Success, "未示教时上游匹配仍完成");
            var baselinePoses = (PoseSet)initial.RuntimeOutputs![matcher.Id]["poses"]!;
            var reference = HalconPositionCorrectionExecutor.Teach(baselinePoses);
            Fails(initial, "REFERENCE_REQUIRED");
            correction = With(correction, PositionReferenceParameters.Key, reference);
            flow = flow with { Nodes = [reader, matcher, correction, threshold] };
            var identity = Run(templatePath); Check(identity.Status == WorkflowRunStatus.Success, "恒等图像完成完整链路");
            using (var original = new HImage(templatePath))
            using (var difference = original.AbsDiffImage((HImage)identity.RuntimeOutputs![correction.Id]["image"]!, 1d))
            using (var domain = difference.GetDomain())
            { difference.MinMaxGray(domain, 0, out double min, out double max, out double range); Check(max < 1, "恒等变换保持像素"); }
            DisposeRun(identity);
            var moved = Run(rotatedPath); Check(moved.Status == WorkflowRunStatus.Success, string.Join(";", moved.Errors));
            var corrected = (HImage)moved.RuntimeOutputs![correction.Id]["image"]!;
            corrected.WriteImage("png", 0, Path.Combine(output, "corrected.png"));
            using (var expected = new HImage(templatePath))
            using (var region = new HRegion(95d, 105d, 210d, 220d))
            using (var difference = corrected.AbsDiffImage(expected, 1d))
            { using var mean = difference.Intensity(region, out HTuple deviation); using var dd = deviation; Check(mean.D < 8, $"旋转图像已对齐：平均灰度差 {mean.D:F3}"); }
            var transform = (CoordinateTransform2D)moved.RuntimeOutputs[correction.Id]["transform"]!;
            var current = ((PoseSet)moved.RuntimeOutputs[matcher.Id]["poses"]!).Images.Single().Matches.Single();
            using (var matrix = new HTuple(transform.CurrentToReference))
            {
                HOperatorSet.AffineTransPoint2d(matrix, current.Row, current.Column, out HTuple r, out HTuple c); using var rr = r; using var cc = c;
                Check(Math.Abs(r.D - reference.Pose.Row) < 1e-6 && Math.Abs(c.D - reference.Pose.Column) < 1e-6, "标准点映射到基准");
            }
            var vm = new FlowTabViewModel(flow, registry, validator);
            var same = DetectionOverlayScene.Create(vm, moved, new(Guid.NewGuid(), correction.Id, Overlays: [new(threshold.Id)]), 600, 480);
            Check(same.Geometry.Count > 0 && same.Captions.All(c => !c.Text.Contains("不匹配")), "矫正图允许下游区域叠加");
            var cross = DetectionOverlayScene.Create(vm, moved, new(Guid.NewGuid(), reader.Id, Overlays: [new(threshold.Id)]), 600, 480);
            Check(cross.Geometry.Count == 0 && cross.Captions.Any(c => c.Text.Contains("不匹配")), "原图拒绝矫正空间区域");
            var metadata = moved.SpatialOutputs!;
            Check(metadata[reader.Id]["image"].SpaceId != metadata[correction.Id]["image"].SpaceId, "矫正输出新坐标空间");
            var context = new NodeExecutionContext(correction, new Dictionary<string, JsonElement>(), CancellationToken.None, _ => { },
                new Dictionary<string, object?> { ["image"] = moved.RuntimeOutputs[reader.Id]["image"], ["poses"] = baselinePoses }, moved.Id,
                new Dictionary<string, FrameReference> { ["image"] = metadata[reader.Id]["image"] });
            try { new HalconPositionCorrectionExecutor().ExecuteAsync(context).GetAwaiter().GetResult(); throw new Exception("旧位姿未拒绝"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("FRAME_MISMATCH"), "旧帧位姿拒绝"); }
            var currentPoses = (PoseSet)moved.RuntimeOutputs[matcher.Id]["poses"]!;
            var currentContext = context with { RuntimeInputs = new Dictionary<string, object?> { ["image"] = moved.RuntimeOutputs[reader.Id]["image"], ["poses"] = currentPoses } };
            var otherFrame = metadata[reader.Id]["image"] with { FrameId = Guid.NewGuid() };
            try { new HalconPositionCorrectionExecutor().ExecuteAsync(currentContext with { InputFrames = new Dictionary<string, FrameReference> { ["image"] = otherFrame } }).GetAwaiter().GetResult(); throw new Exception("错帧未拒绝"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("FRAME_MISMATCH"), "同尺寸错帧拒绝"); }
            try { new HalconPositionCorrectionExecutor().ExecuteAsync(currentContext with { InputFrames = new Dictionary<string, FrameReference> { ["image"] = metadata[reader.Id]["image"] with { Annotated = true } } }).GetAwaiter().GetResult(); throw new Exception("标注图未拒绝"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("ANNOTATED_IMAGE"), "标注预览拒绝"); }
            var invalidPose = currentPoses with { Images = [new(1, [current with { Row = double.NaN }])] };
            try { new HalconPositionCorrectionExecutor().ExecuteAsync(currentContext with { RuntimeInputs = new Dictionary<string, object?> { ["image"] = moved.RuntimeOutputs[reader.Id]["image"], ["poses"] = invalidPose } }).GetAwaiter().GetResult(); throw new Exception("无效位姿未拒绝"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("POSE_INVALID"), "非有限位姿拒绝"); }
            DisposeRun(moved);
            Fails(Run(blankPath), "POSE_NOT_FOUND");
            var saved = flow;
            flow = flow with { Nodes = flow.Nodes.Select(n => n.Id == correction.Id ? With(n, "limitMotion", true) : n).ToList() };
            Fails(Run(rotatedPath), "POSE_OUT_OF_RANGE"); flow = saved;
            flow = flow with { Nodes = flow.Nodes.Select(n => n.Id == matcher.Id ? With(n, "templateRoi", "92,102,213,223") : n).ToList() };
            Fails(Run(rotatedPath), "REFERENCE_STALE"); flow = saved;
            flow = flow with { Nodes = flow.Nodes.Select(n => n.Id == matcher.Id ? With(n, "maximumMatches", 2) : n).ToList() };
            Fails(Run(Path.Combine(Path.GetFullPath(args[0]), "multiple.png")), "POSE_AMBIGUOUS"); flow = saved;
            Fails(Run(Path.Combine(Path.GetFullPath(args[0]), "input")), "SINGLE_IMAGE_REQUIRED");
            var line = With(Node(HalconNodeProvider.StraightLineFind, 810, 250), "roi", "0,0,479,599");
            flow = flow with { Nodes = [.. flow.Nodes, line], Edges = [.. flow.Edges, new(Guid.NewGuid(), correction.Id, "image", line.Id, "image")] };
            var border = Run(rotatedPath); Check(Result(border, line.Id).Error?.Contains("ROI_OUTSIDE_VALID_IMAGE") == true, "直线范围缺失在执行前拦截"); DisposeRun(border); flow = saved;
            foreach (var typeId in new[] { HalconNodeProvider.Classification, HalconNodeProvider.SemanticSegmentation, HalconNodeProvider.ObjectDetection })
            {
                var detector = With(With(Node(typeId, 810, 250), "modelPackage", "not-loaded-in-border-check"), "rois",
                    new[] { new DetectionRoi(Guid.NewGuid(), "越界检查", 0, 0, 479, 599) });
                flow = saved with { Nodes = [.. saved.Nodes, detector], Edges = [.. saved.Edges, new(Guid.NewGuid(), correction.Id, "image", detector.Id, "image")] };
                var invalidRoi = Run(rotatedPath);
                Check(Result(invalidRoi, detector.Id).Error?.Contains("ROI_OUTSIDE_VALID_IMAGE") == true, typeId + "在加载模型前拦截无效ROI"); DisposeRun(invalidRoi);
            }
            flow = saved;
            var passthrough = Node(BuiltInTestNodeProvider.PassThrough, 750, 220);
            flow = saved with { Nodes = [.. saved.Nodes, passthrough], Edges = [.. saved.Edges.Where(e => e.ToNodeId != threshold.Id),
                new(Guid.NewGuid(), correction.Id, "image", passthrough.Id, "input"), new(Guid.NewGuid(), passthrough.Id, "output", threshold.Id, "image")] };
            var through = Run(rotatedPath);
            Check(through.Status == WorkflowRunStatus.Success && through.SpatialOutputs![passthrough.Id]["output"].Corrected, "透传保留矫正图像来源"); DisposeRun(through); flow = saved;
            var outside = With(With(Node(HalconNodeProvider.Classification, 810, 250), "modelPackage", "not-loaded-in-border-check"), "rois",
                new[] { new DetectionRoi(Guid.NewGuid(), "不允许裁小", -5, 100, 210, 220) });
            flow = saved with { Nodes = [.. saved.Nodes, outside], Edges = [.. saved.Edges, new(Guid.NewGuid(), correction.Id, "image", outside.Id, "image")] };
            var outsideRun = Run(templatePath); Check(Result(outsideRun, outside.Id).Error?.Contains("不能自动裁小") == true, "原始ROI超出画布不静默裁小"); DisposeRun(outsideRun); flow = saved;
            for (var index = 0; index < 200; index++)
            {
                if (index % 50 == 0) MemorySamples.Add(System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64);
                if (index % 10 == 0) { models.Clear(); Check(models.Count == 0, "模拟停止后模型为空"); }
                var repeated = Run(index % 2 == 0 ? rotatedPath : blankPath);
                Check(Result(repeated, correction.Id).Status == (index % 2 == 0 ? NodeExecutionStatus.Success : NodeExecutionStatus.Failed), "正负样本连续交替"); DisposeRun(repeated);
            }
            MemorySamples.Add(System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64);
            using (var cancel = new CancellationTokenSource())
            { cancel.Cancel(); using var cancelled = engine.RunAsync(flow, cancellationToken: cancel.Token).GetAwaiter().GetResult(); Check(cancelled.Status == WorkflowRunStatus.Cancelled, "取消不执行节点"); }
            Check(HalconPositionCorrectionExecutor.NormalizeAngle(-358) == 2 && HalconPositionCorrectionExecutor.NormalizeAngle(358) == -2, "角度跨180归一化");
            foreach (var angle in new[] { -90d, -22d, 0d, 22d, 90d, 179d, -179d })
            {
                var theta = angle * Math.PI / 180; var point = new QuickMatchPose(253.25, 303.75, angle, .9); var target = new QuickMatchPose(150.5, 160.5, 0, 1);
                using var matrix = PositionTransform.Standard(point, target); HOperatorSet.HomMat2dInvert(matrix, out HTuple inverted); using var inverse = inverted;
                var row = 210d; var col = 250d; var values = matrix.DArr;
                var expectedRow = Math.Cos(theta) * (row - point.Row) + Math.Sin(theta) * (col - point.Column) + target.Row;
                var expectedCol = -Math.Sin(theta) * (row - point.Row) + Math.Cos(theta) * (col - point.Column) + target.Column;
                Check(Math.Abs(values[0] * row + values[1] * col + values[2] - expectedRow) < 1e-6 &&
                      Math.Abs(values[3] * row + values[4] * col + values[5] - expectedCol) < 1e-6, "独立三角公式检查旋转方向");
                HOperatorSet.AffineTransPoint2d(matrix, row, col, out HTuple r, out HTuple c); using var rr = r; using var cc = c;
                HOperatorSet.AffineTransPoint2d(inverse, r, c, out HTuple backR, out HTuple backC); using var br = backR; using var bc = backC;
                Check(Math.Abs(backR.D - row) < 1e-6 && Math.Abs(backC.D - col) < 1e-6, "正逆变换精度");
                // Independently place a circular pixel marker using explicit forward trigonometry.
                var desiredRow = 175d; var desiredCol = 185d;
                var observedRow = Math.Cos(theta) * (desiredRow - target.Row) - Math.Sin(theta) * (desiredCol - target.Column) + point.Row;
                var observedCol = Math.Sin(theta) * (desiredRow - target.Row) + Math.Cos(theta) * (desiredCol - target.Column) + point.Column;
                using var markerRegion = new HRegion(); markerRegion.GenCircle(observedRow, observedCol, 8d);
                using var black = new HImage(); black.GenImageConst("byte", 600, 480);
                using var marked = black.PaintRegion(markerRegion, 255d, "fill"); using var adjusted = PositionTransform.ForPixels(matrix);
                using var aligned = PositionTransform.Warp(marked, adjusted, 600, 480); using var foundMarker = aligned.Threshold(128d, 255d);
                using var markerArea = foundMarker.AreaCenter(out HTuple markerRow, out HTuple markerCol); using var mr = markerRow; using var mc = markerCol;
                Check(Math.Abs(markerRow.D - desiredRow) <= 1 && Math.Abs(markerCol.D - desiredCol) <= 1, "独立像素标志对齐误差不超过1像素");
            }
            var store = new ProjectStore(); var path = Path.Combine(output, "位置矫正检查.hvproj");
            var workspace = store.CreateProject(path, "位置矫正检查"); store.SaveProject(workspace, [flow]);
            var restored = store.OpenProject(path).Project.Flows.Single();
            Check(restored.Nodes.Single(n => n.Id == correction.Id).Parameters[PositionReferenceParameters.Key].Deserialize<PositionReference>() == reference, "示教保存恢复");
            var copy = store.CopyFlow(workspace, flow, "副本"); var copiedReference = copy.Nodes.Single(n => n.TypeId == correction.TypeId).Parameters[PositionReferenceParameters.Key].Deserialize<PositionReference>()!;
            Check(copy.Nodes.Single(n => n.TypeId == matcher.TypeId).Id == copiedReference.MatchNodeId, "复制流程示教节点引用重映射");
            Check(WorkflowConfiguration.NodeSignature(flow, correction.Id) == WorkflowConfiguration.NodeSignature(copy, copy.Nodes.Single(n => n.TypeId == correction.TypeId).Id), "完整复制不改变图像配置身份");
            var changedGeometry = flow with { Nodes = flow.Nodes.Select(n => n.Id == correction.Id ? With(n, PositionReferenceParameters.Key,
                reference with { Pose = reference.Pose with { Row = reference.Pose.Row + 1 } }) : n).ToList() };
            Check(WorkflowConfiguration.NodeSignature(flow, correction.Id) != WorkflowConfiguration.NodeSignature(changedGeometry, correction.Id), "重新示教几何使下游配置身份变化");
            using (var clonedRun = engine.RunAsync(copy).GetAwaiter().GetResult()) Check(clonedRun.Status == WorkflowRunStatus.Success, "复制流程可执行");
            var importedPath = Path.Combine(output, "exported.flow.json"); File.WriteAllText(importedPath, JsonSerializer.Serialize(flow));
            var imported = store.ImportFlow(workspace, importedPath);
            using (var importedRun = engine.RunAsync(imported).GetAwaiter().GetResult()) Check(importedRun.Status == WorkflowRunStatus.Success, "导入流程基准引用可执行");
            var workspaceVm = new WorkflowWorkspaceViewModel();
            try
            {
                workspaceVm.OpenProject(path);
                HObject[] previousObjects = [];
                for (var i = 0; i < 20; i++)
                {
                    var run = workspaceVm.RunSelectedFlowAsync().GetAwaiter().GetResult(); workspaceVm.SetExecutionRun(run);
                    Check(run.Status == WorkflowRunStatus.Success && previousObjects.All(o => !o.IsInitialized()), "工作区启停及旧输出替换");
                    previousObjects = run.RuntimeOutputs!.Values.SelectMany(v => v.Values).OfType<HObject>().ToArray();
                    workspaceVm.ReleaseModelSessions();
                }
                workspaceVm.OpenProject(path);
                Check(workspaceVm.LastRun is null && previousObjects.All(o => !o.IsInitialized()), "切换项目释放结果");
                var running = workspaceVm.RunSelectedFlowAsync();
                workspaceVm.ShutdownAsync().GetAwaiter().GetResult();
                var finished = running.GetAwaiter().GetResult();
                Check(finished.RuntimeOutputs?.Values.SelectMany(v => v.Values).OfType<HObject>().All(o => !o.IsInitialized()) != false, "执行中关闭等待并释放");
            }
            finally { workspaceVm.ShutdownAsync().GetAwaiter().GetResult(); }
            if (args.Length >= 3)
            {
                var productProject = store.OpenProject(Path.GetFullPath(args[2])).Project;
                var productFlow = productProject.Flows.Single(f => f.Nodes.Any(n => n.TypeId == HalconNodeProvider.ObjectDetection));
                var productReader = productFlow.Nodes.Single(n => n.TypeId == HalconNodeProvider.ImageRead);
                var detector = productFlow.Nodes.Single(n => n.TypeId == HalconNodeProvider.ObjectDetection);
                var folder = productReader.Parameters["path"].GetString()!;
                var file = Directory.EnumerateFiles(folder, "*.jpg").Order(StringComparer.OrdinalIgnoreCase).First();
                using var productImage = new HImage(file); productImage.GetImageSize(out int pw, out int ph);
                var referencePath = Path.Combine(output, "product-reference.png"); productImage.WriteImage("png", 0, referencePath);
                var inputReader = With(reader, "path", referencePath);
                var locator = With(With(With(With(matcher, "templateImage", referencePath), "templateRoi", $"{ph / 4},{pw / 4},{ph * 3 / 4},{pw * 3 / 4}"), "angleStart", -15d), "angleExtent", 30d);
                var align = With(correction, PositionReferenceParameters.Key, null);
                var product = new FlowDocument { Name = "P1–P6位置矫正链路", Nodes = [inputReader, locator, align, detector],
                    Edges = [new(Guid.NewGuid(), reader.Id, "image", matcher.Id, "image"), new(Guid.NewGuid(), reader.Id, "image", correction.Id, "image"),
                        new(Guid.NewGuid(), matcher.Id, "poses", correction.Id, "poses"), new(Guid.NewGuid(), correction.Id, "image", detector.Id, "image")],
                    DetectionPanels = [new(Guid.NewGuid(), align.Id, Contents: [new(Guid.NewGuid(), align.Id, Overlays: [new(detector.Id, Fields: [])])])] };
                using (var teaching = engine.RunAsync(product).GetAwaiter().GetResult())
                {
                    Check(Result(teaching, matcher.Id).Status == NodeExecutionStatus.Success, "真实产品模板匹配：" + string.Join(";", teaching.Errors));
                    var productReference = HalconPositionCorrectionExecutor.Teach((PoseSet)teaching.RuntimeOutputs![matcher.Id]["poses"]!);
                    align = With(align, PositionReferenceParameters.Key, productReference);
                }
                product = product with { Nodes = [inputReader, locator, align, detector] };
                using (var baseline = engine.RunAsync(product).GetAwaiter().GetResult())
                {
                    Check(baseline.Status == WorkflowRunStatus.Success, "真实P1-P6基准推理：" + string.Join(";", baseline.Errors));
                    Check(Result(baseline, detector.Id).Outputs["roiResults"].GetArrayLength() == 6, "基准六ROI实际推理");
                    File.WriteAllText(Path.Combine(output, "product-baseline.json"), JsonSerializer.Serialize(baseline.NodeResults, new JsonSerializerOptions { WriteIndented = true }));
                }
                var a = 3 * Math.PI / 180; var cos = Math.Cos(a); var sin = Math.Sin(a); var cr = (ph - 1) / 2d; var cl = (pw - 1) / 2d;
                using var known = new HTuple(new double[] { cos, -sin, cr + 12 - cos * cr + sin * cl, sin, cos, cl + 10 - sin * cr - cos * cl });
                using var pixels = PositionTransform.ForPixels(known); using var shifted = PositionTransform.Warp(productImage, pixels, pw, ph);
                var shiftedPath = Path.Combine(output, "product-shifted.png"); shifted.WriteImage("png", 0, shiftedPath);
                var shiftedFlow = product with { Nodes = product.Nodes.Select(n => n.Id == reader.Id ? With(n, "path", shiftedPath) : n).ToList() };
                using (var detected = engine.RunAsync(shiftedFlow, configurationFlow: product).GetAwaiter().GetResult())
                {
                    Check(detected.Status == WorkflowRunStatus.Success, "真实P1-P6偏移3度图完整推理：" + string.Join(";", detected.Errors));
                    Check(Result(detected, detector.Id).Outputs["roiResults"].GetArrayLength() == 6, "偏移后六ROI实际推理");
                    File.WriteAllText(Path.Combine(output, "product-shifted.json"), JsonSerializer.Serialize(detected.NodeResults, new JsonSerializerOptions { WriteIndented = true }));
                    ((HImage)detected.RuntimeOutputs![detector.Id]["preview"]!).WriteImage("png", 0, Path.Combine(output, "product-result.png"));
                }
                var productPath = Path.Combine(output, "P1–P6位置矫正检查.hvproj");
                store.SaveProject(new(output, new ProjectDocument { Name = "P1–P6位置矫正检查", Flows = [product], DefaultFlowId = product.Id.ToString("N") }, productPath));
            }
            models.Clear(); dl.Clear(); Check(models.Count == 0 && dl.Count == 0, "检查结束模型清空");
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { checks = _checks, Passed, privateBytesSamples = MemorySamples }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} 项位置矫正检查"); return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
}
