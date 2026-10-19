using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        if (args.Length < 3) { Console.Error.WriteLine("用法：<原六ROI项目> <检测模型目录> <新输出目录> [--limit=12] [--confidence=0.5]"); return 2; }
        var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        try
        {
            var store = new ProjectStore(); var original = store.OpenProject(Path.GetFullPath(args[0]));
            var sourceFlow = original.Project.Flows.First(flow => flow.Nodes.Any(node => node.Parameters.ContainsKey("rois")));
            var rois = DetectionRoi.Parse(sourceFlow.Nodes.First(node => node.Parameters.ContainsKey("rois")).Parameters["rois"]);
            var folder = sourceFlow.Nodes.Single(node => node.TypeId == HalconNodeProvider.ImageRead).Parameters["path"].GetString()!;
            string Option(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? fallback;
            var limit = int.Parse(Option("--limit", "12"), CultureInfo.InvariantCulture);
            var confidence = double.Parse(Option("--confidence", "0.5"), CultureInfo.InvariantCulture);
            if (limit < 1) throw new ArgumentException("样本数量必须大于零。");
            var files = Directory.EnumerateFiles(folder, Option("--pattern", "*")).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff").Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var chosen = Enumerable.Range(0, Math.Min(limit, files.Length)).Select(i => files[(int)Math.Round(i * (files.Length - 1d) / Math.Max(1, Math.Min(limit, files.Length) - 1))]).Distinct().ToArray();
            if (chosen.Length == 0) throw new InvalidDataException("没有测试图片。");
            var detector = new NodeInstance { TypeId = HalconNodeProvider.ObjectDetection, DisplayName = "P1–P6目标检测", X = 350, Y = 100,
                Parameters = new() { ["modelPackage"] = JsonSerializer.SerializeToElement(Path.GetFullPath(args[1])), ["rois"] = JsonSerializer.SerializeToElement(rois),
                    ["expectedClass"] = JsonSerializer.SerializeToElement("$roi"), ["minimumConfidence"] = JsonSerializer.SerializeToElement(confidence),
                    ["minimumCount"] = JsonSerializer.SerializeToElement(1), ["maximumCount"] = JsonSerializer.SerializeToElement(1) } };
            var reader = new NodeInstance { TypeId = HalconNodeProvider.ImageRead, DisplayName = "读取原图文件夹", X = 70, Y = 100, Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(folder) } };
            var flow = new FlowDocument { Name = "P1–P6 HALCON目标检测", Nodes = [reader, detector], Edges = [new(Guid.NewGuid(), reader.Id, "image", detector.Id, "image")],
                DetectionPanels = [new(Guid.NewGuid(), reader.Id, Contents: [new(Guid.NewGuid(), reader.Id, Overlays: [new(detector.Id, Fields: [])])])] };
            var project = new ProjectDocument { Name = "原图六位置目标检测", Flows = [flow], DefaultFlowId = flow.Id.ToString("N") };
            var projectPath = Path.Combine(output, "原图六位置目标检测.hvproj"); store.SaveProject(new(output, project, projectPath), [flow]);
            var reopened = store.OpenProject(projectPath).Project.Flows.Single();
            if (!DetectionRoi.Parse(reopened.Nodes.Single(n => n.Id == detector.Id).Parameters["rois"]).SequenceEqual(rois) || reopened.Edges.Count != 1)
                throw new InvalidDataException("项目保存恢复不一致。");
            using var cache = new DlModelSessionCache(1); using var camera = new HalconCameraManager();
            var registry = BuiltInNodeCatalog.Create(); registry.Register(new HalconNodeProvider(),
                [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(camera), new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor(),
                 new HalconStraightLineFindExecutor(), new HalconSemanticSegmentationExecutor(cache), new HalconClassificationExecutor(cache), new HalconObjectDetectionExecutor(cache), new HalconQuickMatchExecutor(), new HalconPositionCorrectionExecutor()]);
            var engine = new WorkflowExecutionEngine(registry, new WorkflowGraphValidator(registry));
            using var csv = new StreamWriter(Path.Combine(output, "results.csv"), false, new UTF8Encoding(true));
            using var jsonl = new StreamWriter(Path.Combine(output, "results.jsonl"));
            csv.WriteLine("图像,ROI,期望类别,判定,接受目标数,候选数,最高类别,最高得分,推理毫秒");
            var all = new List<RoiObjectDetectionResult>(); var errors = new List<object>(); var timings = new List<double>(); var completed = 0;
            foreach (var file in chosen)
            {
                var input = reader with { Parameters = new(reader.Parameters) { ["path"] = JsonSerializer.SerializeToElement(file) } };
                using var run = engine.RunAsync(flow with { Nodes = [input, detector] }).GetAwaiter().GetResult();
                try
                {
                    if (run.Status != WorkflowRunStatus.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, run.Errors.Concat(run.NodeResults.Where(n => n.Error is not null).Select(n => n.Error))));
                    var result = run.NodeResults.Single(n => n.NodeId == detector.Id);
                    var rows = result.Outputs["roiResults"].Deserialize<List<RoiObjectDetectionResult>>()!;
                    if (rows.Count != rois.Count(r => r.Enabled)) throw new InvalidDataException("ROI 数量不一致。");
                    foreach (var row in rows)
                    {
                        foreach (var item in row.Candidates)
                            if (item.Bounds.RowBegin < row.Bounds.RowBegin - .500001 || item.Bounds.ColumnBegin < row.Bounds.ColumnBegin - .500001 ||
                                item.Bounds.RowEnd > row.Bounds.RowEnd + .500001 || item.Bounds.ColumnEnd > row.Bounds.ColumnEnd + .500001)
                                throw new InvalidDataException("检测框超出原 ROI 坐标。");
                        if (row.Objects.Any(item => item.ClassName != row.ExpectedClass && !item.ClassName.StartsWith("NG", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("错误类别被接受。");
                        var best = row.Candidates.FirstOrDefault();
                        csv.WriteLine(string.Join(",", Q(Path.GetFileName(file)), Q(row.RoiName), Q(row.ExpectedClass ?? ""), row.Decision.Code, row.Objects.Count, row.Candidates.Count,
                            Q(best?.ClassName ?? ""), best?.Confidence.ToString("R", CultureInfo.InvariantCulture) ?? "", row.InferenceMilliseconds.ToString("F3", CultureInfo.InvariantCulture)));
                    }
                    all.AddRange(rows); timings.Add(run.NodeResults.Sum(n => n.ElapsedMilliseconds));
                    jsonl.WriteLine(JsonSerializer.Serialize(new { image = Path.GetFileName(file), nodes = run.NodeResults, roiResults = rows }));
                    if (completed < 3) SaveOverlay(file, (MultiRoiOverlay)run.RuntimeOutputs![detector.Id]["roiOverlay"]!, Path.Combine(output, $"example-{completed + 1:D2}.png"));
                    var objects = run.RuntimeOutputs!.Values.SelectMany(v => v.Values).OfType<HObject>().ToArray(); run.Dispose();
                    if (objects.Any(item => item.IsInitialized())) throw new InvalidOperationException("原生输出没有释放。");
                    completed++; Console.WriteLine($"{completed}/{chosen.Length} {Path.GetFileName(file)} " + string.Join("; ", rows.Select(r => $"{r.RoiName}:{r.Decision.Code}({r.Objects.Count})")));
                }
                catch (Exception error) { errors.Add(new { image = Path.GetFileName(file), error = error.ToString() }); Console.Error.WriteLine(error); }
                csv.Flush(); jsonl.Flush(); File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new { completed, total = chosen.Length, errors = errors.Count }));
            }
            var lifecycle = new Dictionary<string, bool>();
            var firstInput = reader with { Parameters = new(reader.Parameters) { ["path"] = JsonSerializer.SerializeToElement(chosen[0]) } };
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                using var cancelled = engine.RunAsync(flow with { Nodes = [firstInput, detector] }, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
                lifecycle["cancelBeforeRun"] = cancelled.Status == WorkflowRunStatus.Cancelled && cancelled.RuntimeOutputs?.Count == 0;
            }
            var high = detector with { Parameters = new(detector.Parameters) { ["minimumConfidence"] = JsonSerializer.SerializeToElement(1d) } };
            using (var empty = engine.RunAsync(flow with { Nodes = [firstInput, high] }).GetAwaiter().GetResult())
            {
                var value = empty.NodeResults.Single(n => n.NodeId == detector.Id);
                lifecycle["emptyClearsOldObjects"] = empty.Status == WorkflowRunStatus.Success && value.Outputs["count"].GetInt32() == 0 &&
                    value.Outputs["roiResults"].Deserialize<List<RoiObjectDetectionResult>>()!.All(r => r.Objects.Count == 0 && r.Decision.Code == ClassificationDecision.NotDetected);
            }
            using (var reset = engine.RunAsync(flow with { Nodes = [firstInput, detector] }).GetAwaiter().GetResult())
            {
                var value = reset.NodeResults.Single(n => n.NodeId == detector.Id);
                lifecycle["thresholdResetOnCachedModel"] = reset.Status == WorkflowRunStatus.Success && value.Outputs["count"].GetInt32() > 0 && value.Outputs["modelInfo"].GetProperty("cacheHit").GetBoolean();
            }
            var invalid = detector with { Parameters = new(detector.Parameters) { ["expectedClass"] = JsonSerializer.SerializeToElement("__invalid_class_for_lifecycle__") } };
            using (var failed = engine.RunAsync(flow with { Nodes = [firstInput, invalid] }).GetAwaiter().GetResult())
                lifecycle["failureNoStaleOutputAndEvictsSession"] = failed.Status == WorkflowRunStatus.Failed && !failed.RuntimeOutputs!.ContainsKey(detector.Id) && cache.Count == 0;
            cache.Clear(); lifecycle["cacheCleared"] = cache.Count == 0;
            if (lifecycle.Values.Any(passed => !passed)) errors.Add(new { image = "lifecycle", error = JsonSerializer.Serialize(lifecycle) });
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { completed, errors, roiPredictions = all.Count, confidence, model = Path.GetFullPath(args[1]), sourceProject = Path.GetFullPath(args[0]),
                cacheAfterClear = cache.Count, projectRoundTrip = true, lifecycle, averageWallMs = timings.Count > 0 ? timings.Average() : 0,
                steadyWallMs = timings.Count > 1 ? timings.Skip(1).Average() : 0,
                groups = all.GroupBy(r => r.RoiName).Select(g => new { roi = g.Key, count = g.Count(), decisions = g.GroupBy(r => r.Decision.Code).ToDictionary(v => v.Key, v => v.Count()), accepted = g.Sum(r => r.Objects.Count) }),
                note = "实际节点链路与ROI结果，不是独立精度报告；可能包含训练同源图。" }, Json));
            return errors.Count == 0 ? 0 : 1;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static void SaveOverlay(string file, MultiRoiOverlay overlay, string output)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(Path.GetFullPath(file)); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
        var scale = Math.Min(1, 1800d / bitmap.PixelWidth); var visual = new DrawingVisual();
        using (var d = visual.RenderOpen())
        {
            d.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale));
            foreach (var label in overlay.Rois)
            {
                var box = label.Bounds; var brush = label.Decision?.Code switch { ClassificationDecision.Ok => Brushes.LimeGreen, ClassificationDecision.DetectedNg => Brushes.DodgerBlue, _ => Brushes.Red };
                d.DrawRectangle(null, new Pen(brush, 2), new Rect(box.ColumnBegin * scale, box.RowBegin * scale, (box.ColumnEnd - box.ColumnBegin) * scale, (box.RowEnd - box.RowBegin) * scale));
                var text = new FormattedText($"{label.Name}: {label.Summary}", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 16, brush, 1);
                var p = new Point(Math.Clamp(box.ColumnBegin * scale, 0, Math.Max(0, bitmap.PixelWidth * scale - text.Width)), Math.Max(0, box.RowBegin * scale - text.Height - 3));
                d.DrawRectangle(Brushes.Black, null, new Rect(p, new Size(text.Width + 4, text.Height))); d.DrawText(text, p);
            }
        }
        var render = new RenderTargetBitmap((int)(bitmap.PixelWidth * scale), (int)(bitmap.PixelHeight * scale), 96, 96, PixelFormats.Pbgra32); render.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render)); using var stream = File.Create(output); encoder.Save(stream);
    }
}
