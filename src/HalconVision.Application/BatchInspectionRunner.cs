using HalconVision.Domain;
using HalconVision.Persistence;

namespace HalconVision.Application;

public sealed class BatchInspectionRunner(
    IInspectionEngine engine,
    ResultStore resultStore,
    BatchResultStore batchResultStore)
{
    public BatchInspectionResult Run(
        IReadOnlyList<string> imagePaths,
        InspectionRecipe recipe,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentNullException.ThrowIfNull(recipe);
        if (imagePaths.Count == 0)
            throw new ArgumentException("批量检测至少需要一张图像。", nameof(imagePaths));

        var batchId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}";
        var startedAt = DateTime.UtcNow;
        var items = new List<BatchInspectionItem>(imagePaths.Count);

        for (var index = 0; index < imagePaths.Count; index++)
        {
            var imagePath = imagePaths[index];
            try
            {
                var result = engine.Run(imagePath, recipe);
                var recordPath = resultStore.Save(imagePath, recipe, result);
                items.Add(new BatchInspectionItem(
                    imagePath,
                    result.IsPass ? InspectionStatus.Ok : InspectionStatus.Ng,
                    result.ComponentCount,
                    result.ElapsedMilliseconds,
                    recordPath,
                    null));
            }
            catch (Exception ex)
            {
                items.Add(new BatchInspectionItem(imagePath, InspectionStatus.Error, null, null, null, ex.ToString()));
            }

            progress?.Report(index + 1);
        }

        var summary = new BatchInspectionResult(batchId, recipe.Name, startedAt, DateTime.UtcNow, items);
        batchResultStore.Save(summary);
        return summary;
    }
}
