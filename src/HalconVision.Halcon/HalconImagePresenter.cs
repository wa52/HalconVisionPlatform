using HalconDotNet;
using HalconVision.Domain;

namespace HalconVision.Halcon;

public sealed class HalconImagePresenter
{
    public ImageDimensions Display(string imagePath, HWindow window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(window);
        using var image = new HImage(imagePath);
        image.GetImageSize(out int width, out int height);
        window.ClearWindow();
        window.DispObj(image);
        return new ImageDimensions(width, height);
    }
}
