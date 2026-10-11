using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Persistence;

public sealed class RecipeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HalconVisionPlatform",
        "recipes");

    public string Save(InspectionRecipe recipe, string filePath)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(recipe, JsonOptions));
        File.Move(tempPath, fullPath, overwrite: true);
        return fullPath;
    }

    public InspectionRecipe Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return JsonSerializer.Deserialize<InspectionRecipe>(stream, JsonOptions)
               ?? throw new InvalidDataException("方案文件为空或格式无效。");
    }
}
