using HalconVision.Domain;

namespace HalconVision.Application;

public interface IInspectionEngine
{
    InspectionResult Run(string imagePath, InspectionRecipe recipe);
}
