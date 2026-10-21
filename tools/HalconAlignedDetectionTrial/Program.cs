using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("用法：<已配置四节点方案> <全新输出目录>"); return 2; }
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("拒绝覆盖已有输出。"); return 2; }
        Directory.CreateDirectory(output);
        var checks = new List<string>(); var observations = new List<object>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            var original = Path.GetFullPath(args[0]); var originalHash = Hash(original);
            var store = new ProjectStore(); var workspace = store.OpenProject(original); var flow = workspace.Project.Flows.Single();
            NodeInstance Node(string type) => flow.Nodes.Single(n => n.TypeId == type);
            var reader = Node(HalconNodeProvider.ImageRead); var matcher = Node(HalconNodeProvider.QuickMatch);
            var correction = Node(HalconNodeProvider.PositionCorrection); var detector = Node(HalconNodeProvider.ObjectDetection);
            Check(flow.Nodes.Count == 4, "验证方案恰好包含原图、匹配、矫正和检测四节点");
            var rois = DetectionRoi.Parse(detector.Parameters["rois"]).Where(r => r.Enabled).ToArray();
            Check(rois.Length == 6 && rois.Select(r => r.Name).SequenceEqual(new[] { "P1", "P2", "P3", "P4", "P5", "P6" }), "启用六个命名检测区域");
            var model = detector.Parameters["modelPackage"].GetString()!;
            if (!Directory.Exists(model)) model = Path.GetDirectoryName(model)!;
            var modelHashes = new[] { "model.hdl", "preprocess.hdict", "manifest.json" }.ToDictionary(n => Path.Combine(model, n), n => Hash(Path.Combine(model, n)));
            using var models = new ShapeMatchModelCache(1); using var sessions = new DlModelSessionCache(1); using var camera = new HalconCameraManager();
            var registry = BuiltInNodeCatalog.Create(); registry.Register(new HalconNodeProvider(),
                [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(camera), new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor(),
                 new HalconStraightLineFindExecutor(), new HalconSemanticSegmentationExecutor(sessions), new HalconClassificationExecutor(sessions),
                 new HalconObjectDetectionExecutor(sessions), new HalconQuickMatchExecutor(models), new HalconPositionCorrectionExecutor()]);
            var validator = new WorkflowGraphValidator(registry); var engine = new WorkflowExecutionEngine(registry, validator);
            Check(validator.Validate(flow).Count == 0, "完整方案图校验通过");
            var source = reader.Parameters["path"].GetString()!;
            var files = Directory.Exists(source) ? Directory.EnumerateFiles(source, "OR_*.jpg").Order(StringComparer.OrdinalIgnoreCase).Take(3).ToArray() : [source];
            Check(files.Length > 0 && files.All(File.Exists), "存在真实原图输入");
            WorkflowExecutionRun Run(string path) => engine.RunAsync(flow with { Nodes = flow.Nodes.Select(n => n.Id == reader.Id
                ? n with { Parameters = new(n.Parameters) { ["path"] = JsonSerializer.SerializeToElement(path) } } : n).ToList() }, configurationFlow: flow).GetAwaiter().GetResult();
            void Release(WorkflowExecutionRun run)
            {
                var native = run.RuntimeOutputs?.Values.SelectMany(v => v.Values).OfType<HObject>().ToArray() ?? [];
                run.Dispose(); Check(native.All(n => !n.IsInitialized()), "逐帧原生输出释放");
            }
            void Inspect(WorkflowExecutionRun run, string name)
            {
                Check(run.Status == WorkflowRunStatus.Success && run.NodeResults.All(n => n.Status == NodeExecutionStatus.Success), "四节点执行完成：" + name);
                Check(run.SpatialOutputs![correction.Id]["image"].Corrected, "检测输入使用已矫正坐标空间");
                var rows = run.NodeResults.Single(n => n.NodeId == detector.Id).Outputs["roiResults"].Deserialize<RoiObjectDetectionResult[]>()!;
                Check(rows.Length == 6 && rows.Select(r => r.RoiId).SequenceEqual(rois.Select(r => r.Id)), "六ROI身份及顺序保持");
                Check(rows.All(r => r.ExpectedClass == r.RoiName), "逐ROI匹配对应期望类别");
                Check(rows.All(r => r.Objects.Concat(r.Candidates).All(o => InBounds(o.Bounds, r.Bounds))), "检测框在矫正图对应ROI像素边界内");
                observations.Add(new { image = name, run.Status, milliseconds = (run.EndedAtUtc - run.StartedAtUtc).TotalMilliseconds,
                    offsets = run.NodeResults.Single(n => n.NodeId == correction.Id).Outputs["offsets"],
                    regions = rows.Select(r => new { r.RoiName, r.Decision, accepted = r.Objects.Count, candidates = r.Candidates.Count }) });
                File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(run.NodeResults, Json));
            }
            foreach (var file in files)
            {
                var run = Run(file);
                try { Inspect(run, Path.GetFileNameWithoutExtension(file)); }
                finally { Release(run); }
                Console.WriteLine("完成 " + Path.GetFileName(file));
            }
            var reference = correction.Parameters["reference"].Deserialize<PositionReference>()!;
            var blankPath = Path.Combine(output, "blank-input.png");
            using (var blank = new HImage()) { blank.GenImageConst("byte", reference.Width, reference.Height); blank.WriteImage("png", 0, blankPath); }
            var absent = Run(blankPath);
            try
            {
                var rejected = absent.NodeResults.Single(n => n.NodeId == correction.Id);
                Check(absent.Status == WorkflowRunStatus.Failed && rejected.Status == NodeExecutionStatus.Failed && rejected.Error!.Contains("POSE_NOT_FOUND"), "空图无位姿时位置矫正明确失败");
                Check(absent.NodeResults.Single(n => n.NodeId == detector.Id).Status == NodeExecutionStatus.Skipped && !absent.RuntimeOutputs!.ContainsKey(detector.Id), "位姿失败阻断真实检测节点，检测无旧框输出");
                File.WriteAllText(Path.Combine(output, "missing-pose.json"), JsonSerializer.Serialize(absent.NodeResults, Json));
            }
            finally { Release(absent); }
            var recovered = Run(files[0]); try { Inspect(recovered, "recovered"); } finally { Release(recovered); }
            Check(sessions.Count == 1 && models.Count == 1, "恢复后模型缓存有界");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); using var cancelled = engine.RunAsync(flow, cancellationToken: cancel.Token).GetAwaiter().GetResult();
                Check(cancelled.Status == WorkflowRunStatus.Cancelled && cancelled.RuntimeOutputs?.Count == 0, "启动前取消无输出");
            }
            var copyPath = Path.Combine(output, "1111_四节点检查.hvproj");
            foreach (var asset in flow.AssetReferences)
            {
                var destination = Path.GetFullPath(Path.Combine(output, asset));
                if (!destination.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源路径越界。");
                var origin = Path.GetFullPath(Path.Combine(workspace.RootPath, asset));
                if (!origin.StartsWith(Path.GetFullPath(workspace.RootPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("源资源路径越界。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(origin, destination);
            }
            store.SaveProject(new(output, workspace.Project, copyPath), [flow]); var reopened = store.OpenProject(copyPath).Project.Flows.Single();
            Check(reopened.Nodes.Single(n => n.Id == detector.Id).Parameters["rois"].GetRawText() == detector.Parameters["rois"].GetRawText() &&
                reopened.Nodes.Single(n => n.Id == correction.Id).Parameters["reference"].GetRawText() == correction.Parameters["reference"].GetRawText(), "保存恢复六ROI与位置基准");
            models.Clear(); sessions.Clear(); Check(models.Count == 0 && sessions.Count == 0, "检查结束清空两类模型缓存");
            Check(Hash(original) == originalHash && modelHashes.All(p => Hash(p.Key) == p.Value), "源方案及模型文件未改写");
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { checks = checks.Count, passed = checks, observations,
                sourceProject = original, model, note = "执行成功与ROI规则结果分开记录；不是精度报告，不推导产品真实合格。" }, Json));
            Console.WriteLine($"PASS {checks.Count} 项完整四节点检查"); return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool InBounds(Rectangle2D box, Rectangle2D roi) => new[] { box.RowBegin, box.RowEnd, box.ColumnBegin, box.ColumnEnd }.All(double.IsFinite) &&
        box.RowBegin <= box.RowEnd && box.ColumnBegin <= box.ColumnEnd && box.RowBegin >= roi.RowBegin - .51 && box.RowEnd <= roi.RowEnd + .51 &&
        box.ColumnBegin >= roi.ColumnBegin - .51 && box.ColumnEnd <= roi.ColumnEnd + .51;
}
