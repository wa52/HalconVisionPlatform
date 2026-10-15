using System.Diagnostics;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;

if (args.Length != 3)
{
    Console.Error.WriteLine("用法：HalconVision.SemanticChecks <模型目录> <数据集.hdict> <结果目录>");
    Environment.ExitCode = 2;
    return;
}
var modelDirectory = Path.GetFullPath(args[0]);
var datasetFile = Path.GetFullPath(args[1]);
var outputDirectory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outputDirectory);
var evidence = new List<object>();
try
{
    using var dataset = new HDict(datasetFile, "convert_json_arrays_to", "tuple");
    using var samples = dataset.GetDictTuple("samples");
    using var imageRoot = dataset.GetDictTuple("image_dir");
    using var split = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "split.json")));
    var indices = split.RootElement.GetProperty("test").EnumerateArray().Take(3).Select(value => value.GetInt32()).ToArray();
    if (indices.Length == 0) throw new InvalidDataException("训练模型的 test 划分没有样本。");
    using var cameras = new HalconCameraManager();
    var registry = new NodeRegistry();
    registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(), new HalconCameraCaptureExecutor(cameras),
        new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor(), new HalconStraightLineFindExecutor(), new HalconSemanticSegmentationExecutor(), new HalconClassificationExecutor()]);
    var engine = new WorkflowExecutionEngine(registry, new WorkflowGraphValidator(registry));
    var definition = registry.Definitions.Single(item => item.TypeId == HalconNodeProvider.SemanticSegmentation);
    for (var i = 0; i < indices.Length; i++)
    {
        using var sample = samples.TupleSelect(indices[i]);
        HOperatorSet.GetDictTuple(sample, "image_file_name", out var imageName);
        using (imageName)
        {
            var imagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(datasetFile)!, imageRoot.S, imageName.S));
            Console.WriteLine($"BEGIN {i + 1}: {imagePath}");
            var reader = new NodeInstance { TypeId = HalconNodeProvider.ImageRead, DisplayName = "读取图像",
                Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(imagePath) } };
            var segmenter = new NodeInstance { TypeId = HalconNodeProvider.SemanticSegmentation, DisplayName = "语义分割检测",
                Parameters = definition.Parameters.ToDictionary(item => item.Id, item => item.DefaultValue!.Value) };
            segmenter.Parameters["modelPackage"] = JsonSerializer.SerializeToElement(modelDirectory);
            var flow = new FlowDocument { Name = "真实语义分割检查", Nodes = [reader, segmenter],
                Edges = [new(Guid.NewGuid(), reader.Id, "image", segmenter.Id, "image")] };
            var timer = Stopwatch.StartNew();
            using var run = await engine.RunAsync(flow);
            foreach (var log in run.Logs) Console.WriteLine(log);
            if (run.Status != WorkflowRunStatus.Success)
                throw new InvalidOperationException(string.Join(Environment.NewLine, run.Errors.Concat(run.NodeResults.Select(node => node.Error ?? ""))));
            var result = run.NodeResults.Single(node => node.NodeId == segmenter.Id);
            var native = run.RuntimeOutputs![segmenter.Id];
            var mask = (HImage)native["mask"]!;
            var preview = (HImage)native["preview"]!;
            mask.GetImageSize(out int width, out int height);
            using var maskType = mask.GetImageType();
            if (maskType.S != "byte") throw new InvalidOperationException("标签掩膜不是可导出的 byte 图像。");
            using var inputImage = new HImage(imagePath);
            inputImage.GetImageSize(out int inputWidth, out int inputHeight);
            if (width != inputWidth || height != inputHeight) throw new InvalidOperationException("掩膜尺寸不匹配。");
            mask.WriteImage("png", 0, Path.Combine(outputDirectory, $"{i + 1}-mask.png"));
            preview.WriteImage("png", 0, Path.Combine(outputDirectory, $"{i + 1}-preview.png"));
            var objects = run.RuntimeOutputs.Values.SelectMany(value => value.Values).OfType<HObject>().Distinct().ToArray();
            var item = new { imagePath, width, height, wallMs = timer.Elapsed.TotalMilliseconds, result.Outputs };
            evidence.Add(item);
            Console.WriteLine(JsonSerializer.Serialize(item));
            run.Dispose();
            if (objects.Any(value => value.IsInitialized())) throw new InvalidOperationException("运行结果未释放。");
            Console.WriteLine($"PASS {i + 1}: inference + original dimensions + native output disposal");
        }
    }
    File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new { status = "PASS", evidence }, new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception ex)
{
    Console.WriteLine(ex);
    File.WriteAllText(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
    Environment.ExitCode = 1;
}
