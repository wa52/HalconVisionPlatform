using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HalconDotNet;
using HalconVision.App;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 3) { Environment.ExitCode = 2; return; }
        var modelDirectory = Path.GetFullPath(args[0]);
        var datasetFile = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outputDirectory);
        void Log(string message) => File.AppendAllText(Path.Combine(outputDirectory, "ui-check.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "manifest.json")));
            if (manifest.RootElement.GetProperty("status").GetString() != "reload_and_evaluation_passed")
                throw new InvalidDataException("模型尚未完成训练后评估。");
            using var split = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "split.json")));
            var firstIndex = split.RootElement.GetProperty("test")[0].GetInt32();
            using var dataset = new HDict(datasetFile, "convert_json_arrays_to", "tuple");
            using var samples = dataset.GetDictTuple("samples");
            using var root = dataset.GetDictTuple("image_dir");
            using var sample = samples.TupleSelect(firstIndex);
            HOperatorSet.GetDictTuple(sample, "image_file_name", out var imageName);
            using var name = imageName;
            var imagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(datasetFile)!, root.S, imageName.S));
            var readerDefinition = new HalconNodeProvider().GetDefinitions().Single(item => item.TypeId == HalconNodeProvider.ImageRead);
            var segmentationDefinition = new HalconNodeProvider().GetDefinitions().Single(item => item.TypeId == HalconNodeProvider.SemanticSegmentation);
            var reader = new NodeInstance { TypeId = readerDefinition.TypeId, DisplayName = "读取图像", X = 80, Y = 120,
                Parameters = new() { ["path"] = JsonSerializer.SerializeToElement(imagePath) } };
            var segmentation = new NodeInstance { TypeId = segmentationDefinition.TypeId, DisplayName = "NH320 语义分割", X = 350, Y = 120,
                Parameters = segmentationDefinition.Parameters.ToDictionary(item => item.Id, item => item.DefaultValue!.Value) };
            segmentation.Parameters["modelPackage"] = JsonSerializer.SerializeToElement(modelDirectory);
            var flow = new FlowDocument { Name = "NH320 最新模型检测", Nodes = [reader, segmentation],
                Edges = [new(Guid.NewGuid(), reader.Id, "image", segmentation.Id, "image")],
                DetectionPanels = [new(Guid.NewGuid(), reader.Id, Contents:
                    [new(Guid.NewGuid(), reader.Id, Overlays: [new(segmentation.Id, Fields: ["count", "area"])])])] };
            var project = new ProjectDocument { Name = "NH320 最新模型检测", Flows = [flow], DefaultFlowId = flow.Id.ToString("N") };
            var projectPath = Path.Combine(outputDirectory, "NH320_最新模型检测.hvproj");
            new ProjectStore().SaveProject(new(outputDirectory, project, projectPath));
            Log($"MODEL {modelDirectory}; PROJECT {projectPath}; IMAGE {imagePath}");
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(projectPath);
            app.MainWindow = window;
            app.DispatcherUnhandledException += (_, e) => { Log(e.Exception.ToString()); Environment.ExitCode = 1; };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Delay(250);
                    var vm = (WorkflowWorkspaceViewModel)window.DataContext;
                    vm.SelectedFlow!.SelectNode(segmentation.Id, false);
                    Field<ToggleButton>(window, "DetectionModeToggle").IsChecked = true;
                    window.UpdateLayout();
                    // Invoke the real single-run command; algorithms and display remain production code.
                    typeof(MainWindow).GetMethod("Run_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
                    while (typeof(MainWindow).GetField("_executionTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is Task)
                        await Task.Delay(100);
                    if (vm.LastRun?.Status != WorkflowRunStatus.Success) throw new InvalidOperationException(string.Join("\n", vm.LastRun?.Errors ?? ["未返回运行结果"]));
                    var result = vm.LastRun.NodeResults.Single(item => item.NodeId == segmentation.Id);
                    var dashboard = Field<DetectionDashboard>(window, "DetectionDashboard");
                    var view = dashboard.Tiles.Single().Contents.Single();
                    view.Refresh(); window.UpdateLayout(); await Task.Delay(200);
                    var canvas = Field<Canvas>(view, "_overlayCanvas");
                    var count = result.Outputs["count"].GetInt32();
                    if (count > 0 && !canvas.Children.OfType<System.Windows.Shapes.Polyline>().Any())
                        throw new InvalidOperationException("有目标但图像上没有区域轮廓。");
                    if (!canvas.Children.OfType<TextBlock>().Any(item => item.Text.Contains("数量")) ||
                        !canvas.Children.OfType<TextBlock>().Any(item => item.Text.Contains("面积")))
                        throw new InvalidOperationException("图像上缺少数量或面积结果。");
                    var native = vm.LastRun.RuntimeOutputs![segmentation.Id];
                    ((HImage)native["preview"]!).WriteImage("png", 0, Path.Combine(outputDirectory, "preview.png"));
                    ((HImage)native["mask"]!).WriteImage("png", 0, Path.Combine(outputDirectory, "mask.png"));
                    File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new {
                        modelDirectory, imagePath, projectPath, result.Outputs,
                        renderedContours = canvas.Children.OfType<System.Windows.Shapes.Polyline>().Count(),
                        captions = canvas.Children.OfType<TextBlock>().Select(item => item.Text).ToArray()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Save(window, Path.Combine(outputDirectory, "detection-result-ui.png"));
                    vm.SelectedFlow.MarkSaved();
                    Log($"PASS: latest-model inference; {count} objects; real WPF overlay/captions rendered.");
                    if (!args.Contains("--keep-open")) window.Close();
                }
                catch (Exception ex) { Log("FAIL: " + ex); Environment.ExitCode = 1; window.Close(); }
            };
            window.Show(); app.Run();
        }
        catch (Exception ex) { Log("FAIL: " + ex); Environment.ExitCode = 1; }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
