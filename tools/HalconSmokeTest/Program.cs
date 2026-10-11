using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;

var halconRoot = Environment.GetEnvironmentVariable("HALCONROOT");
var halconArch = Environment.GetEnvironmentVariable("HALCONARCH");
var versionPath = string.IsNullOrWhiteSpace(halconRoot) ? null : Path.Combine(halconRoot, "version.txt");
var nativeDllPath = string.IsNullOrWhiteSpace(halconRoot) || string.IsNullOrWhiteSpace(halconArch)
    ? null
    : Path.Combine(halconRoot, "bin", halconArch, "halcon.dll");
var imagesRoot = Environment.GetEnvironmentVariable("HALCONIMAGES");
var imagePath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(imagesRoot ?? string.Empty, "a01.png");

Console.WriteLine("HALCON .NET runtime probe");
Console.WriteLine($"TimestampUtc: {DateTimeOffset.UtcNow:O}");
Console.WriteLine($"HALCONROOT: {halconRoot ?? "<unset>"}");
Console.WriteLine($"HALCONARCH: {halconArch ?? "<unset>"}");
Console.WriteLine($"HALCONIMAGES: {imagesRoot ?? "<unset>"}");
Console.WriteLine($"HALCON installation version: {ReadTextFile(versionPath) ?? "<missing>"}");
Console.WriteLine($"HALCON version file: {DescribeFile(versionPath)}");
Console.WriteLine($"HALCON managed interface: {DescribeAssembly(typeof(HImage).Assembly)}");
Console.WriteLine($"HALCON native runtime: {DescribeFile(nativeDllPath)}");
Console.WriteLine($".NET runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
Console.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Process bitness: {IntPtr.Size * 8}-bit");
Console.WriteLine($"HALCON-related PATH entries: {string.Join(";", (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Where(p => p.Contains("HALCON", StringComparison.OrdinalIgnoreCase) || p.Contains("MVTec", StringComparison.OrdinalIgnoreCase)))}");
Console.WriteLine($"Input image: {imagePath}");
Console.WriteLine($"Input image exists: {File.Exists(imagePath)}");

try
{
    if (!File.Exists(imagePath))
        throw new FileNotFoundException("The requested HALCON sample/input image was not found.", imagePath);

    Console.WriteLine("STEP read_image: starting");
    using var image = new HImage(imagePath);
    image.GetImageSize(out int width, out int height);
    Console.WriteLine($"STEP read_image: OK; image size = {width} x {height}");

    Console.WriteLine("STEP threshold: starting (gray range 128..255)");
    using var regions = image.Threshold(128.0, 255.0);
    Console.WriteLine("STEP threshold: OK");

    Console.WriteLine("STEP connection: starting");
    using var connected = regions.Connection();
    Console.WriteLine("STEP connection: OK");

    Console.WriteLine("STEP count_obj: starting");
    var count = connected.CountObj();
    Console.WriteLine($"STEP count_obj: OK; object count = {count}");

    Console.WriteLine("STEP workflow_preview_contract: starting");
    var registry = BuiltInNodeCatalog.Create();
    registry.Register(new HalconNodeProvider(), [new HalconImageReadExecutor(), new HalconThresholdExecutor(), new HalconBlobAnalysisExecutor()]);
    var imageNode = new NodeInstance
    {
        TypeId = HalconNodeProvider.ImageRead,
        DisplayName = "读取图像",
        Parameters = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(imagePath) }
    };
    var thresholdNode = new NodeInstance
    {
        TypeId = HalconNodeProvider.Threshold,
        DisplayName = "二值化",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["minimum"] = JsonSerializer.SerializeToElement(128d),
            ["maximum"] = JsonSerializer.SerializeToElement(255d)
        }
    };
    var flow = new FlowDocument
    {
        Nodes = [imageNode, thresholdNode],
        Edges = [new FlowEdge(Guid.NewGuid(), imageNode.Id, "image", thresholdNode.Id, "image")]
    };
    var engine = new WorkflowExecutionEngine(registry, new WorkflowGraphValidator(registry));
    using var run = await engine.RunAsync(flow);
    if (run.Status != WorkflowRunStatus.Success) throw new InvalidOperationException($"Workflow failed: {string.Join("; ", run.Errors)}");
    Console.WriteLine("STEP workflow nodes: OK; image read and threshold both report success");
    var runtimeOutputs = run.RuntimeOutputs
        ?? throw new InvalidOperationException("Workflow reports success but returns no runtime outputs for node image preview.");
    if (!runtimeOutputs.TryGetValue(thresholdNode.Id, out var thresholdOutputs) ||
        !thresholdOutputs.TryGetValue("region", out var thresholdValue) || thresholdValue is not HRegion thresholdRegion)
        throw new InvalidOperationException("The successful threshold node did not retain an HRegion for preview.");
    var workflowRegionCount = thresholdRegion.CountObj();
    Console.WriteLine($"STEP workflow preview outputs: OK; threshold HRegion remains usable after RunAsync; object count = {workflowRegionCount}");
    Console.WriteLine("RESULT: SUCCESS");
}
catch (Exception ex)
{
    Console.Error.WriteLine("RESULT: FAILURE");
    Console.Error.WriteLine("FULL_EXCEPTION_BEGIN");
    Console.Error.WriteLine(ex.ToString());
    Console.Error.WriteLine("FULL_EXCEPTION_END");
    Console.Error.WriteLine($"Loaded HALCON modules: {string.Join(Environment.NewLine, FindLoadedModules(halconRoot))}");
    Environment.ExitCode = 1;
}

Console.WriteLine("Loaded HALCON modules:");
foreach (var module in FindLoadedModules(halconRoot))
    Console.WriteLine($"  {module}");

static string DescribeAssembly(Assembly assembly)
{
    var path = assembly.Location;
    var fileVersion = string.IsNullOrWhiteSpace(path) ? "<unknown>" : FileVersionInfo.GetVersionInfo(path).FileVersion;
    return $"{path}; assembly={assembly.GetName().Version}; file={fileVersion}";
}

static string DescribeFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return $"{path ?? "<unknown>"}; MISSING";

    var info = new FileInfo(path);
    return $"{info.FullName}; file={FileVersionInfo.GetVersionInfo(path).FileVersion}; size={info.Length}";
}

static string? ReadTextFile(string? path) =>
    !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllText(path).Trim() : null;

static IReadOnlyList<string> FindLoadedModules(string? halconRoot)
{
    try
    {
        var root = string.IsNullOrWhiteSpace(halconRoot) ? null : Path.GetFullPath(halconRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var results = new List<string>();
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            var path = module.FileName;
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var underHalconRoot = root is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            var managedHalcon = module.ModuleName.StartsWith("MVTec.HalconDotNet", StringComparison.OrdinalIgnoreCase);
            if (underHalconRoot || managedHalcon)
                results.Add($"{path}; file={FileVersionInfo.GetVersionInfo(path).FileVersion}");
        }
        return results.Count > 0 ? results : ["<no HALCON modules found in process module list>"];
    }
    catch (Exception ex)
    {
        return [$"module enumeration failed: {ex.GetType().FullName}: {ex.Message}"];
    }
}
