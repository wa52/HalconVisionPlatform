using HalconDotNet;

namespace HalconVision.Halcon;

/// <summary>Owns a node's RGB preview until it is transferred to the execution result.</summary>
internal sealed class OwnedPreviewImage : IDisposable
{
    private HImage? _image;
    private readonly CancellationToken _cancellation;

    public OwnedPreviewImage(HImage source, CancellationToken cancellation)
    {
        _cancellation = cancellation;
        cancellation.ThrowIfCancellationRequested();
        using var channels = source.CountChannels();
        if (channels.I is not (1 or 3)) throw new InvalidDataException("检测预览仅支持灰度或 RGB 图像。");
        // Copy each plane separately: Compose3 alone aliases its inputs, and the
        // source itself may have shared channels. Only these private matrices may be overpainted.
        using var redSource = source.AccessChannel(1);
        using var greenSource = source.AccessChannel(channels.I == 1 ? 1 : 2);
        using var blueSource = source.AccessChannel(channels.I == 1 ? 1 : 3);
        using var red = redSource.CopyImage();
        using var green = greenSource.CopyImage();
        using var blue = blueSource.CopyImage();
        _image = red.Compose3(green, blue);
    }

    public void PaintMargin(HRegion region, HTuple color)
    {
        var image = _image ?? throw new ObjectDisposedException(nameof(OwnedPreviewImage));
        _cancellation.ThrowIfCancellationRequested();
        image.OverpaintRegion(region, color, "margin");
    }

    public HImage TakeImage()
    {
        _cancellation.ThrowIfCancellationRequested();
        var image = _image ?? throw new ObjectDisposedException(nameof(OwnedPreviewImage));
        _image = null; return image;
    }

    public void Dispose() => Interlocked.Exchange(ref _image, null)?.Dispose();
}
