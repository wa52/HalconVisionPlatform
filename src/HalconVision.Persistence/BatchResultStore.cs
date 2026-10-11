using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Persistence;

public sealed class BatchResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HalconVisionPlatform",
        "batches");

    public string Save(BatchInspectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(DirectoryPath);
        var filePath = Path.Combine(DirectoryPath, $"{result.BatchId}.json");
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(result, JsonOptions));
        File.Move(tempPath, filePath, overwrite: true);
        return filePath;
    }
}
