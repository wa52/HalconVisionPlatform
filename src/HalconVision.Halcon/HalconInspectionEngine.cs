using System.Diagnostics;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconInspectionEngine : IInspectionEngine
{
    public InspectionResult Run(string imagePath, InspectionRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(recipe);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("检测图像不存在。", imagePath);

        var timer = Stopwatch.StartNew();
        using var image = new HImage(imagePath);
        image.GetImageSize(out int imageWidth, out int imageHeight);
        recipe.Validate(imageWidth, imageHeight);

        using var roi = new HRegion();
        roi.GenRectangle1(
            (double)recipe.RoiY,
            (double)recipe.RoiX,
            (double)(recipe.RoiY + recipe.RoiHeight - 1),
            (double)(recipe.RoiX + recipe.RoiWidth - 1));
        using var roiImage = image.ReduceDomain(roi);
        using var thresholded = roiImage.Threshold(recipe.ThresholdMinimum, recipe.ThresholdMaximum);
        using var connected = thresholded.Connection();
        using var filtered = connected.SelectShape("area", "and", recipe.MinimumArea, recipe.MaximumArea);

        var count = filtered.CountObj();
        var boxes = new List<DetectedComponent>(count);
        for (var index = 1; index <= count; index++)
        {
            using var component = filtered.SelectObj(index);
            component.SmallestRectangle1(out int top, out int left, out int bottom, out int right);
            boxes.Add(new DetectedComponent(left, top, right - left + 1, bottom - top + 1));
        }

        timer.Stop();
        return new InspectionResult(
            count,
            recipe.ExpectedComponentCount,
            count == recipe.ExpectedComponentCount,
            timer.Elapsed.TotalMilliseconds,
            boxes);
    }
}
