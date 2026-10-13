using System.Security.Cryptography;
using System.Text.Json;

namespace HalconVision.Halcon;

internal sealed record SemanticModelPackage(string DirectoryPath, int[] ClassIds, string[] ClassNames)
{
    public string ModelPath => Path.Combine(DirectoryPath, "model.hdl");
    public string PreprocessPath => Path.Combine(DirectoryPath, "preprocess.hdict");

    public static SemanticModelPackage Load(string selectedPath, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) throw new InvalidDataException("请先选择训练平台导出的模型目录。");
        var path = Path.GetFullPath(selectedPath);
        var directory = System.IO.Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        if (!System.IO.Directory.Exists(path) && (!File.Exists(path) || Path.GetFileName(path) is not ("manifest.json" or "model.hdl")))
            throw new InvalidDataException("请选择模型目录或其中的 manifest.json / model.hdl。");
        foreach (var name in new[] { "manifest.json", "model.hdl", "preprocess.hdict" })
            if (!File.Exists(Path.Combine(directory, name))) throw new FileNotFoundException($"模型目录缺少 {name}。", Path.Combine(directory, name));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var root = manifest.RootElement;
        if (!root.TryGetProperty("task", out var task) || task.GetString() != "semantic_segmentation")
            throw new InvalidDataException("模型目录不是 HalconTrainingPlatform 语义分割模型。");
        var (ids, names) = ReadClasses(root);
        var classesPath = Path.Combine(directory, "classes.json");
        if (File.Exists(classesPath))
        {
            using var classes = JsonDocument.Parse(File.ReadAllText(classesPath));
            var (otherIds, otherNames) = ReadClasses(classes.RootElement);
            if (!ids.SequenceEqual(otherIds) || !names.SequenceEqual(otherNames))
                throw new InvalidDataException("classes.json 与 manifest.json 的类别定义不一致。");
        }
        var checksumPath = Path.Combine(directory, "checksums.json");
        if (File.Exists(checksumPath))
        {
            using var hashes = JsonDocument.Parse(File.ReadAllText(checksumPath));
            foreach (var file in new[] { "model.hdl", "preprocess.hdict", "classes.json" }.Where(name => File.Exists(Path.Combine(directory, name))))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!hashes.RootElement.TryGetProperty(file, out var expected)) throw new InvalidDataException($"校验清单缺少 {file}。");
                using var stream = File.OpenRead(Path.Combine(directory, file));
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(expected.GetString(), actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"模型文件校验失败：{file}。请重新选择完整训练输出目录。");
            }
        }
        return new(directory, ids, names);
    }

    private static (int[], string[]) ReadClasses(JsonElement root)
    {
        var ids = root.GetProperty("classIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var names = root.GetProperty("classNames").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
        if (ids.Length == 0 || ids.Length != names.Length || ids.Distinct().Count() != ids.Length || ids.Any(id => id is < 0 or > 255))
            throw new InvalidDataException("类别定义无效：类别 ID 必须唯一且在 0–255 之间，并与名称一一对应。");
        return (ids, names);
    }
}
