using System.Text.Json;
using HalconVision.Domain;

namespace HalconVision.Persistence;

public sealed class ResultStore
{
    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HalconVisionPlatform",
        "results");

    public string Save(string imagePath, InspectionRecipe recipe, InspectionResult result)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(DirectoryPath);
        var runId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}";
        var imageName = runId + Path.GetExtension(imagePath);
        File.Copy(imagePath, Path.Combine(DirectoryPath, imageName));
        var jsonPath = Path.Combine(DirectoryPath, runId + ".json");
        var payload = new { runId, image = imageName, recipe, result, completedAtUtc = DateTime.UtcNow };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return jsonPath;
    }
}
