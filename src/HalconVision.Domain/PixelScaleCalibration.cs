using System.Text.Json.Serialization;

namespace HalconVision.Domain;

public sealed record PixelScaleCalibration(double ReferencePixels, double ReferenceMillimeters,
    double MillimetersPerPixel, [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height, bool IsotropicConfirmed,
    string Model = "fixed-isotropic-raw-v1");

public sealed record PixelLengthConversion(double Pixels, double Millimeters, PixelScaleCalibration Calibration,
    [property: JsonPropertyName("width")] int Width, [property: JsonPropertyName("height")] int Height)
{
    public string Unit => "mm";
}

public sealed record UnitConversionStatus(string Code, string Message)
{
    public const string ScaleReady = "PIXEL_SCALE_READY";
    public const string Converted = "LENGTH_CONVERTED";
    public const string Missing = "LENGTH_MISSING";
}
