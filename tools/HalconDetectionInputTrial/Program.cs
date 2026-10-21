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
        if (args.Length < 3 || args[2] is not ("repro" or "probe" or "audit") ||
            args.Length != (args[2] == "audit" ? 5 : 3))
        { Console.Error.WriteLine("用法：<四节点方案> <全新输出目录> <repro|probe|audit> [audit的分类裁图目录及正常对照原图]"); return 2; }
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("拒绝覆盖已有输出。"); return 2; }
        Directory.CreateDirectory(output);
        try
        {
            var input = Path.GetFullPath(args[0]); var inputHash = Hash(input);
            var flow = new ProjectStore().OpenProject(input).Project.Flows.Single();
            var reader = flow.Nodes.Single(n => n.TypeId == HalconNodeProvider.ImageRead);
            var detector = flow.Nodes.Single(n => n.TypeId == HalconNodeProvider.ObjectDetection);
            var correction = flow.Nodes.Single(n => n.TypeId == HalconNodeProvider.PositionCorrection);
            var reference = correction.Parameters["reference"].Deserialize<PositionReference>()!;
            var path = reader.Parameters["path"].GetString()!;
            var file = Directory.Exists(path) ? Directory.EnumerateFiles(path, "OR_*.jpg").Order(StringComparer.OrdinalIgnoreCase).First() : path;
            var model = detector.Parameters["modelPackage"].GetString()!;
            var hashes = new[] { input, file, Path.Combine(model, "model.hdl"), Path.Combine(model, "preprocess.hdict"), Path.Combine(model, "manifest.json") }.ToDictionary(p => p, Hash);
            if (args[2] == "audit") hashes.Add(Path.GetFullPath(args[4]), Hash(args[4]));
            using var shapes = new ShapeMatchModelCache(1); using var sessions = new DlModelSessionCache(1); using var camera = new HalconCameraManager();
            var registry = BuiltInNodeCatalog.Create();
            registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(camera), new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor(),
                new HalconStraightLineFindExecutor(), new HalconSemanticSegmentationExecutor(sessions), new HalconClassificationExecutor(sessions),
                new HalconQuickMatchExecutor(shapes), new HalconPositionCorrectionExecutor(), new HalconObjectDetectionExecutor(sessions)]);
            var engine = new WorkflowExecutionEngine(registry, new WorkflowGraphValidator(registry));
            var rois = DetectionRoi.Parse(detector.Parameters["rois"]);
            var observations = new List<object>(); var reproduced = false;
            RoiObjectDetectionResult[] Run(string name, bool full, double confidence, bool raw = false, int pad = 0, string? sampleFile = null)
            {
                var definitions = rois.Select(r => r with
                {
                    RowBegin = Math.Max(0, r.RowBegin - pad), ColumnBegin = Math.Max(0, r.ColumnBegin - pad),
                    RowEnd = Math.Min(reference.Height - 1, r.RowEnd + pad), ColumnEnd = Math.Min(reference.Width - 1, r.ColumnEnd + pad)
                }).ToArray();
                var node = detector with { Parameters = new(detector.Parameters)
                {
                    ["rois"] = JsonSerializer.SerializeToElement(full ? Array.Empty<DetectionRoi>() : definitions),
                    ["expectedClass"] = JsonSerializer.SerializeToElement(full ? "" : "$roi"),
                    ["maximumCount"] = JsonSerializer.SerializeToElement(full ? 100 : 1),
                    ["minimumConfidence"] = JsonSerializer.SerializeToElement(confidence)
                } };
                var prepared = flow with { Nodes = flow.Nodes.Select(n => n.Id == detector.Id ? node : n.Id == reader.Id
                    ? n with { Parameters = new(n.Parameters) { ["path"] = JsonSerializer.SerializeToElement(sampleFile ?? file) } } : n).ToList() };
                if (raw) prepared = prepared with { Edges = prepared.Edges.Select(e => e.ToNodeId == detector.Id
                    ? e with { FromNodeId = reader.Id, FromPortId = "image" } : e).ToList() };
                using var run = engine.RunAsync(prepared, configurationFlow: flow).GetAwaiter().GetResult();
                if (run.Status != WorkflowRunStatus.Success) throw new InvalidOperationException(string.Join(";", run.NodeResults.Select(n => n.Error)));
                var result = run.NodeResults.Single(n => n.NodeId == detector.Id);
                var rows = result.Outputs["roiResults"].Deserialize<RoiObjectDetectionResult[]>()!;
                var snapshot = new { name, image = sampleFile ?? file, full, confidence, raw, pad, rows, result.Outputs, result.ElapsedMilliseconds };
                File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(snapshot, Json));
                observations.Add(snapshot);
                if (name == "baseline")
                {
                    reproduced = rows.Where(r => r.Decision.Code == ClassificationDecision.NotDetected).Select(r => r.RoiName).Order()
                        .SequenceEqual(new[] { "P1", "P4", "P5", "P6" });
                    if (run.RuntimeOutputs![correction.Id]["image"] is HImage image) image.WriteImage("png", 0, Path.Combine(output, "corrected-input.png"));
                }
                Console.WriteLine(name + ": " + string.Join("; ", rows.Select(r => $"{r.RoiName}:{r.Decision.Code} candidates={r.Candidates.Count} [" +
                    string.Join(",", r.Candidates.Select(c => $"{c.ClassName}={c.Confidence:F4}")) + "]")));
                return rows;
            }
            var baseline = Run("baseline", false, .5);
            if (args[2] == "probe")
            {
                Run("full-frame", true, .5);
                Run("full-frame-raw", true, .5, raw: true);
                Run("roi-low-threshold", false, .05);
                Run("roi-padding-64", false, .5, pad: 64);
            }
            if (args[2] == "audit")
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(model, "manifest.json")));
                var dataset = manifest.RootElement.GetProperty("sourceDataset").GetString()!;
                var annotationsPath = Path.Combine(Path.GetDirectoryName(dataset)!, "annotations.json");
                hashes.Add(annotationsPath, Hash(annotationsPath));
                using var annotations = JsonDocument.Parse(File.ReadAllText(annotationsPath));
                var audit = new List<object>();
                using var aligned = new HImage(Path.Combine(output, "corrected-input.png"));
                foreach (var roi in rois.Where(r => r.Enabled))
                {
                    var top = (int)Math.Floor(roi.RowBegin); var left = (int)Math.Floor(roi.ColumnBegin);
                    var bottom = (int)Math.Ceiling(roi.RowEnd); var right = (int)Math.Ceiling(roi.ColumnEnd);
                    var labelImage = annotations.RootElement.GetProperty("images").EnumerateArray().Single(i =>
                        i.GetProperty("width").GetInt32() == right - left + 1 && i.GetProperty("height").GetInt32() == bottom - top + 1 &&
                        i.GetProperty("file_name").GetString()!.EndsWith("_0.jpg", StringComparison.Ordinal));
                    var imageId = labelImage.GetProperty("id").GetInt32();
                    var boxes = annotations.RootElement.GetProperty("annotations").EnumerateArray().Where(a => a.GetProperty("image_id").GetInt32() == imageId).ToArray();
                    var negative = boxes.Length == 0;
                    var classifiedCrop = Path.Combine(Path.GetFullPath(args[3]), (negative ? "NG" : "") + roi.Name, "0.jpg");
                    var exportedCrop = Path.Combine(Path.GetDirectoryName(annotationsPath)!, "images", labelImage.GetProperty("file_name").GetString()!);
                    hashes.Add(classifiedCrop, Hash(classifiedCrop)); hashes.Add(exportedCrop, Hash(exportedCrop));
                    if (hashes[classifiedCrop] != hashes[exportedCrop]) throw new InvalidDataException(roi.Name + "训练裁图来源哈希不一致。");
                    using var crop = aligned.CropRectangle1(top, left, bottom, right); using var training = new HImage(exportedCrop);
                    using var cropChannels = crop.CountChannels(); using var trainingChannels = training.CountChannels();
                    if (cropChannels.I != trainingChannels.I) throw new InvalidDataException(roi.Name + "裁图通道数不一致。");
                    var differenceMeans = new double[cropChannels.I];
                    using (var domain = crop.GetDomain())
                    {
                        for (var channel = 1; channel <= cropChannels.I; channel++)
                        {
                            using var cropChannel = crop.AccessChannel(channel); using var trainingChannel = training.AccessChannel(channel);
                            using var difference = cropChannel.AbsDiffImage(trainingChannel, 1d);
                            HOperatorSet.Intensity(domain, difference, out var mean, out var deviation);
                            using (mean) using (deviation) differenceMeans[channel - 1] = mean.D;
                        }
                    }
                    if (differenceMeans.Any(v => !double.IsFinite(v) || v > 3)) throw new InvalidDataException(roi.Name + "当前裁图与训练图差异过大，不能建立样本对应关系。");
                    var result = baseline.Single(r => r.RoiName == roi.Name);
                    var agrees = negative ? result.Objects.Count == 0 && result.Decision.Code == ClassificationDecision.NotDetected :
                        result.Objects.Count == 1 && result.Decision.Code == ClassificationDecision.Ok &&
                        boxes.All(b => annotations.RootElement.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == b.GetProperty("category_id").GetInt32()).GetProperty("name").GetString() == roi.Name);
                    if (!agrees) throw new InvalidOperationException(roi.Name + "当前输出与现有样本标签不一致。");
                    audit.Add(new { roi.Name, imageId, exportedCrop, classifiedCrop, negative, boxes = boxes.Length, differenceMeans, result.Decision, labelAgreement = agrees });
                    Console.WriteLine($"标签核对 {roi.Name}: negative={negative}, MAE=" + string.Join(",", differenceMeans.Select(v => v.ToString("F4"))));
                }
                File.WriteAllText(Path.Combine(output, "label-audit.json"), JsonSerializer.Serialize(audit, Json));
                var control = Run("positive-control", false, .5, sampleFile: Path.GetFullPath(args[4]));
                if (control.Length != 6 || control.Any(r => r.Objects.Count != 1 || r.Decision.Code != ClassificationDecision.Ok))
                    throw new InvalidOperationException("正常对照未满足六个ROI的当前检出规则。");
            }
            shapes.Clear(); sessions.Clear();
            if (hashes.Any(p => Hash(p.Key) != p.Value) || Hash(input) != inputHash || shapes.Count != 0 || sessions.Count != 0)
                throw new InvalidOperationException("只读输入或资源释放检查失败。");
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { reproduced, observations, hashes,
                note = "同图输入对照；低阈值仅诊断，不改原项目，不作为精度或真值报告。" }, Json));
            if (args[2] == "repro" && reproduced) { Console.Error.WriteLine("REPRO: P1/P4/P5/P6四个区域未检出。"); return 3; }
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
