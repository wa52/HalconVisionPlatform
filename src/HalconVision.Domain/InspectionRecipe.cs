namespace HalconVision.Domain;

public sealed record InspectionRecipe
{
    public string Name { get; init; } = "新建方案";
    public double ThresholdMinimum { get; init; } = 128;
    public double ThresholdMaximum { get; init; } = 255;
    public double MinimumArea { get; init; } = 20;
    public double MaximumArea { get; init; } = 1000000;
    public int RoiX { get; init; }
    public int RoiY { get; init; }
    public int RoiWidth { get; init; }
    public int RoiHeight { get; init; }
    public int ExpectedComponentCount { get; init; } = 1;

    public void Validate(int imageWidth, int imageHeight)
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("方案名称不能为空。");
        if (!double.IsFinite(ThresholdMinimum) || !double.IsFinite(ThresholdMaximum) ||
            ThresholdMinimum < 0 || ThresholdMaximum > 255 || ThresholdMinimum > ThresholdMaximum)
            throw new InvalidDataException("灰度阈值必须满足 0 ≤ 最小值 ≤ 最大值 ≤ 255。");
        if (!double.IsFinite(MinimumArea) || !double.IsFinite(MaximumArea) ||
            MinimumArea < 0 || MaximumArea < MinimumArea)
            throw new InvalidDataException("面积筛选必须满足 0 ≤ 最小面积 ≤ 最大面积。");
        if (RoiX < 0 || RoiY < 0 || RoiWidth <= 0 || RoiHeight <= 0 ||
            RoiX + RoiWidth > imageWidth || RoiY + RoiHeight > imageHeight)
            throw new InvalidDataException("矩形 ROI 超出图像范围。");
        if (ExpectedComponentCount < 0)
            throw new InvalidDataException("目标连通域数量不能小于 0。");
    }
}
