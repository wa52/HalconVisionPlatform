using HalconVision.Domain;
using HalconVision.Persistence;

namespace HalconVision.Application;

public sealed record PersistedInspection(InspectionResult Result, string RecordPath);

public sealed class InspectionRunner(IInspectionEngine engine, ResultStore resultStore)
{
    public PersistedInspection Run(string imagePath, InspectionRecipe recipe)
    {
        var result = engine.Run(imagePath, recipe);
        var recordPath = resultStore.Save(imagePath, recipe, result);
        return new PersistedInspection(result, recordPath);
    }
}
