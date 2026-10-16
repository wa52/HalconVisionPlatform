using System.Diagnostics;
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
        if (args.Length < 4)
        {
            Console.Error.WriteLine("用法：HalconClassificationTrial <模型目录> <原图文件夹> <ROI文件夹> <输出目录> [--inspect-only]"); return 2;
        }
        var model = Path.GetFullPath(args[0]); var input = Path.GetFullPath(args[1]);
        var references = Path.GetFullPath(args[2]); var output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(output);
        try
        {
            if (args.Contains("--render-examples")) return RenderExamples(input, output);
            string Option(string name, string fallback) => args.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? fallback;
            var threshold = double.Parse(Option("--confidence", "0.8"), CultureInfo.InvariantCulture);
            var runtime = Option("--device", "gpu"); var deviceIndex = int.Parse(Option("--device-index", "0"), CultureInfo.InvariantCulture);
            if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || runtime is not ("gpu" or "cpu") || deviceIndex < 0)
                throw new InvalidDataException("置信度或推理设备参数无效。");
            var paths = Directory.EnumerateFiles(input).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff")
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) throw new InvalidDataException("输入文件夹中没有图像。");
            using var first = new HImage(paths[0]); first.GetImageSize(out int width, out int height);
            var referenceFiles = Directory.EnumerateFiles(references, "P*.hobj").Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var rois = new List<DetectionRoi>(); var details = new List<object>();
            foreach (var path in referenceFiles)
            {
                using var region = new HRegion(); region.ReadRegion(path);
                if (region.CountObj() != 1) throw new InvalidDataException($"{path} 必须为单个 ROI。");
                region.SmallestRectangle1(out int r1, out int c1, out int r2, out int c2);
                var area = region.AreaCenter(out double _, out double _);
                if (r1 < 0 || c1 < 0 || r2 >= height || c2 >= width) throw new InvalidDataException($"{path} 超出原图范围。");
                var name = Path.GetFileNameWithoutExtension(path);
                rois.Add(new(Guid.NewGuid(), name, r1, c1, r2, c2));
                details.Add(new { name, r1, c1, r2, c2, width = c2 - c1 + 1, height = r2 - r1 + 1, rectangleCoverage = area / ((r2 - r1 + 1d) * (c2 - c1 + 1d)) });
                Console.WriteLine($"ROI {name}: ({r1},{c1})–({r2},{c2}), {c2-c1+1}×{r2-r1+1}");
                using var crop = first.CropRectangle1(r1, c1, r2, c2); crop.WriteImage("png", 0, Path.Combine(output, $"first-{name}.png"));
            }
            if (rois.Count == 0) throw new InvalidDataException("未找到 P*.hobj。");
            string? trainingImageRoot = null;
            var training = Path.Combine(model, "training_params.json");
            if (File.Exists(training))
            {
                using var parameters = JsonDocument.Parse(File.ReadAllText(training));
                if (parameters.RootElement.TryGetProperty("dataset", out var dataset) && File.Exists(dataset.GetString()))
                {
                    using var dictionary = new HDict(dataset.GetString()!, "convert_json_arrays_to", "tuple");
                    using var root = dictionary.GetDictTuple("image_dir"); trainingImageRoot = root.S;
                }
            }
            var inspection = new { model, input, references, imageCount = paths.Length, width, height, rois = details, trainingImageRoot };
            File.WriteAllText(Path.Combine(output, "roi-reference.json"), JsonSerializer.Serialize(inspection, Json));
            SaveOverlay(paths[0], rois, null, Path.Combine(output, "roi-reference.png"));
            var node = new NodeInstance { TypeId = HalconNodeProvider.Classification, DisplayName = "六位置分类检测", X = 350, Y = 100,
                Parameters = new() { ["modelPackage"] = JsonSerializer.SerializeToElement(model), ["rois"] = JsonSerializer.SerializeToElement(rois),
                    ["deviceRuntime"] = JsonSerializer.SerializeToElement(runtime), ["deviceIndex"] = JsonSerializer.SerializeToElement(deviceIndex), ["minimumConfidence"] = JsonSerializer.SerializeToElement(threshold) } };
            var reader = new NodeInstance { TypeId = HalconNodeProvider.ImageRead, DisplayName = "读取原图文件夹", X = 70, Y = 100,
                Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(input) } };
            var flow = new FlowDocument { Name = "六位置分类检测", Nodes = [reader, node], Edges = [new(Guid.NewGuid(), reader.Id, "image", node.Id, "image")],
                DetectionPanels = [new(Guid.NewGuid(), reader.Id, Contents: [new(Guid.NewGuid(), reader.Id, Overlays: [new(node.Id, Fields: [])])])] };
            var project = new ProjectDocument { Name = "原图六位置分类试运行", Flows = [flow], DefaultFlowId = flow.Id.ToString("N") };
            new ProjectStore().SaveProject(new(output, project, Path.Combine(output, "原图六位置分类.hvproj")), [flow]);
            if (args.Contains("--inspect-only")) { Console.WriteLine("ROI inspection complete."); return 0; }
            using var cache = new DlModelSessionCache(1);
            var executor = new HalconClassificationExecutor(cache);
            var rows = new List<TrialRow>(); var errors = new List<object>(); var completed = 0; var savedAlerts = 0;
            using var stream = new StreamWriter(Path.Combine(output, "results.jsonl"), false, new UTF8Encoding(false));
            using var csv = new StreamWriter(Path.Combine(output, "results.csv"), false, new UTF8Encoding(true));
            csv.WriteLine("图像,ROI,预测类别,置信度,次高类别,次高置信度,状态,位置类别一致,推理毫秒");
            var totalClock = Stopwatch.StartNew();
            foreach (var path in paths)
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    using var image = new HImage(path); image.GetImageSize(out int w, out int h);
                    if (w != width || h != height) throw new InvalidDataException("图像尺寸与 ROI 参考不一致，不能按原坐标检测。");
                    var context = new NodeExecutionContext(node, new Dictionary<string, JsonElement>(), CancellationToken.None,
                        log => { if (completed == 0) Console.WriteLine(log); }, new Dictionary<string, object?> { ["image"] = image });
                    var produced = executor.ExecuteAsync(context).GetAwaiter().GetResult();
                    using var run = new WorkflowExecutionRun(Guid.NewGuid(), flow.Id, DateTime.UtcNow, DateTime.UtcNow, WorkflowRunStatus.Success, [], [], [],
                        new Dictionary<Guid, IReadOnlyDictionary<string, object?>> { [node.Id] = produced.RuntimePorts! });
                    var results = produced.Ports["roiResults"].Deserialize<List<RoiClassificationResult>>()!;
                    if (results.Count != rois.Count) throw new InvalidDataException("ROI 数量与输出不一致。");
                    foreach (var result in results)
                    {
                        var scores = result.Scores.OrderByDescending(score => score.Confidence).ToArray();
                        var row = new TrialRow(Path.GetFileName(path), result.RoiName, result.ClassName, result.Confidence,
                            scores[1].ClassName, scores[1].Confidence, result.Status.Code,
                            result.ClassName == result.RoiName || result.ClassName == "NG" + result.RoiName, result.InferenceMilliseconds);
                        rows.Add(row);
                        csv.WriteLine(string.Join(",", Quote(row.Image), Quote(row.Roi), Quote(row.ClassName), row.Confidence.ToString("R", CultureInfo.InvariantCulture),
                            Quote(row.SecondClass), row.SecondConfidence.ToString("R", CultureInfo.InvariantCulture), row.Status, row.PositionClassMatches, row.InferenceMilliseconds.ToString("F3", CultureInfo.InvariantCulture)));
                    }
                    stream.WriteLine(JsonSerializer.Serialize(new { image = Path.GetFileName(path), wallMs = clock.Elapsed.TotalMilliseconds, results, modelInfo = produced.Ports["modelInfo"] })); stream.Flush(); csv.Flush();
                    var alert = results.Any(result => result.Confidence < threshold || result.ClassName != result.RoiName && result.ClassName != "NG" + result.RoiName);
                    if (completed < 3 || alert && savedAlerts++ < 5)
                        SaveOverlay(path, rois, results, Path.Combine(output, $"{completed+1:D3}-classification.png"), threshold);
                    var owned = produced.RuntimePorts!.Values.OfType<HObject>().ToArray(); run.Dispose();
                    if (owned.Any(value => value.IsInitialized())) throw new InvalidOperationException("分类输出未释放。");
                    completed++;
                    Console.WriteLine($"{completed}/{paths.Length}: {Path.GetFileName(path)} | " + string.Join("; ", results.Select(result => $"{result.RoiName}={result.ClassName} {result.Confidence:P1}")));
                }
                catch (Exception ex)
                {
                    errors.Add(new { image = Path.GetFileName(path), error = ex.ToString() });
                    stream.WriteLine(JsonSerializer.Serialize(new { image = Path.GetFileName(path), error = ex.ToString() })); stream.Flush();
                    Console.Error.WriteLine($"ERROR {Path.GetFileName(path)}: {ex.Message}");
                }
                File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new { completed, total = paths.Length, errors = errors.Count, elapsedSeconds = totalClock.Elapsed.TotalSeconds }));
            }
            cache.Clear();
            var groups = rows.GroupBy(row => row.Roi).Select(group => new { roi = group.Key, count = group.Count(),
                classes = group.GroupBy(row => row.ClassName).ToDictionary(items => items.Key, items => items.Count()),
                meanConfidence = group.Average(row => row.Confidence), minimumConfidence = group.Min(row => row.Confidence),
                lowConfidence = group.Count(row => row.Confidence < threshold), unexpectedPosition = group.Count(row => !row.PositionClassMatches) }).ToArray();
            var summary = new { status = errors.Count == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS", inspection, completed, errors,
                roiPredictions = rows.Count, threshold, elapsedSeconds = totalClock.Elapsed.TotalSeconds, modelSessionsAfterClear = cache.Count, groups,
                note = "未提供逐张真实标签，不计算准确率；位置类别一致仅表示 Pn/NGPn 对应位置，不代表产品正确判定。原图可能与训练数据同源。" };
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, Json));
            Console.WriteLine(JsonSerializer.Serialize(new { completed, errors = errors.Count, roiPredictions = rows.Count, modelSessionsAfterClear = cache.Count }));
            return errors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); return 1; }
    }
    private static string Quote(string text) => '"' + text.Replace("\"", "\"\"") + '"';
    private static int RenderExamples(string input, string output)
    {
        var workspace = new ProjectStore().OpenProject(Path.Combine(output, "原图六位置分类.hvproj"));
        var node = workspace.Project.Flows.Single().Nodes.Single(item => item.TypeId == HalconNodeProvider.Classification);
        var rois = DetectionRoi.Parse(node.Parameters["rois"]);
        var threshold = node.Parameters["minimumConfidence"].GetDouble();
        var predictions = new List<(string Image, List<RoiClassificationResult> Results)>();
        foreach (var line in File.ReadLines(Path.Combine(output, "results.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("results", out var results))
                predictions.Add((json.RootElement.GetProperty("image").GetString()!, results.Deserialize<List<RoiClassificationResult>>()!));
        }
        if (predictions.Count == 0) throw new InvalidDataException("没有可渲染的推理结果。");
        var normal = predictions.FirstOrDefault(item => item.Results.All(result => result.ClassName == result.RoiName));
        var ng = predictions.FirstOrDefault(item => item.Results.Any(result => result.ClassName.StartsWith("NG", StringComparison.Ordinal)));
        var lowest = predictions.MinBy(item => item.Results.Min(result => result.Confidence));
        foreach (var (name, sample) in new[] { ("example-normal.png", normal), ("example-ng.png", ng), ("example-lowest.png", lowest) })
        {
            if (sample.Image is null) continue;
            SaveOverlay(Path.Combine(input, sample.Image), rois, sample.Results, Path.Combine(output, name), threshold);
            Console.WriteLine($"EXAMPLE {name}: {sample.Image}");
        }
        return 0;
    }
    private static void SaveOverlay(string path, IReadOnlyList<DetectionRoi> rois, IReadOnlyList<RoiClassificationResult>? results, string output, double threshold = .8)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
        var scale = Math.Min(1, 1800d / bitmap.PixelWidth); var width = (int)Math.Round(bitmap.PixelWidth * scale); var height = (int)Math.Round(bitmap.PixelHeight * scale);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, new Rect(0, 0, width, height));
            foreach (var roi in rois)
            {
                var result = results?.First(item => item.RoiId == roi.Id);
                var decision = result is null ? null : result.Decision ?? ClassificationDecision.From(result.ClassName,
                    new ClassificationStatus(result.Confidence < threshold ? ClassificationStatus.LowConfidence : result.Status.Code, result.Status.Message), "NG");
                var color = decision?.Code switch
                {
                    ClassificationDecision.Ok => Brushes.LimeGreen,
                    ClassificationDecision.DetectedNg => Brushes.DodgerBlue,
                    ClassificationDecision.NotDetected => Brushes.Red,
                    _ => Brushes.Gold
                };
                var rectangle = new Rect(roi.ColumnBegin * scale, roi.RowBegin * scale, (roi.ColumnEnd - roi.ColumnBegin + 1) * scale, (roi.RowEnd - roi.RowBegin + 1) * scale);
                drawing.DrawRectangle(null, new Pen(color, 3), rectangle);
                var label = result is null ? roi.Name : $"{roi.Name} → {decision!.Message} · {result.ClassName}  {result.Confidence:P1}";
                var text = new FormattedText(label, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 18, color, 1);
                var location = new Point(Math.Clamp(rectangle.X, 0, Math.Max(0, width - text.Width - 8)), Math.Clamp(rectangle.Y - text.Height - 6, 0, Math.Max(0, height - text.Height - 6)));
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 12, 18, 24)), null, new Rect(location.X, location.Y, text.Width + 8, text.Height + 4));
                drawing.DrawText(text, new Point(location.X + 4, location.Y + 2));
            }
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered)); using var stream = File.Create(output); encoder.Save(stream);
    }
    private sealed record TrialRow(string Image, string Roi, string ClassName, double Confidence, string SecondClass, double SecondConfidence,
        string Status, bool PositionClassMatches, double InferenceMilliseconds);
}
