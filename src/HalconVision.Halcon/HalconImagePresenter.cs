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

    public ImageDimensions Display(HObject iconicObject, HWindow window, int imageWidth, int imageHeight, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(iconicObject);
        ArgumentNullException.ThrowIfNull(window);
        window.ClearWindow();
        Fit(window, imageWidth, imageHeight, viewportWidth, viewportHeight);
        if (iconicObject is HRegion region)
        {
            using var mask = region.RegionToBin(255, 0, imageWidth, imageHeight);
            window.DispObj(mask);
        }
        else
        {
            window.DispObj(iconicObject);
        }
        return new ImageDimensions(imageWidth, imageHeight);
    }

    public void Fit(HWindow window, int imageWidth, int imageHeight, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (imageWidth <= 0 || imageHeight <= 0) return;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            window.SetPart(0, 0, imageHeight - 1, imageWidth - 1);
            return;
        }

        var partWidth = Math.Max(imageWidth, imageHeight * viewportWidth / viewportHeight);
        var partHeight = Math.Max(imageHeight, imageWidth * viewportHeight / viewportWidth);
        var left = (imageWidth - partWidth) / 2;
        var top = (imageHeight - partHeight) / 2;
        window.SetPart((int)Math.Floor(top), (int)Math.Floor(left), (int)Math.Ceiling(top + partHeight) - 1, (int)Math.Ceiling(left + partWidth) - 1);
    }

    public void Zoom(HWindow window, double factor)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(factor) || factor <= 0) return;
        window.GetPart(out int row1, out int column1, out int row2, out int column2);
        var centerRow = (row1 + row2) / 2d;
        var centerColumn = (column1 + column2) / 2d;
        var halfHeight = Math.Max(1, (row2 - row1 + 1) / factor / 2d);
        var halfWidth = Math.Max(1, (column2 - column1 + 1) / factor / 2d);
        window.SetPart((int)(centerRow - halfHeight), (int)(centerColumn - halfWidth), (int)(centerRow + halfHeight), (int)(centerColumn + halfWidth));
    }

    public void Pan(HWindow window, double deltaX, double deltaY, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (viewportWidth <= 0 || viewportHeight <= 0) return;
        window.GetPart(out int row1, out int column1, out int row2, out int column2);
        var columnsPerPixel = (column2 - column1 + 1d) / viewportWidth;
        var rowsPerPixel = (row2 - row1 + 1d) / viewportHeight;
        var shiftColumns = deltaX * columnsPerPixel;
        var shiftRows = deltaY * rowsPerPixel;
        window.SetPart((int)(row1 - shiftRows), (int)(column1 - shiftColumns), (int)(row2 - shiftRows), (int)(column2 - shiftColumns));
    }
}
