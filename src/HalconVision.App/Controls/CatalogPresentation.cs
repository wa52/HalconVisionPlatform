using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HalconVision.App;

public static class CatalogPresentation
{
    public static int CategoryRank(string category) => category switch
    { "图像输入" => 0, "图像处理" => 1, "定位匹配" => 2, "特征检测" => 3,
      "几何测量" => 4, "标定与单位" => 5, "深度学习" => 6, "测试节点" => 99, _ => 90 };
}

/// <summary>Native vector glyphs: no bitmap scaling or font-dependent missing symbols.</summary>
public sealed class CatalogNodeIconConverter : IValueConverter
{
    private static Geometry Icon(string path) { var shape = Geometry.Parse(path); shape.Freeze(); return shape; }
    private static readonly Geometry Image = Icon("M2,4 H22 V20 H2 Z M3,17 L9,10 L14,15 L18,11 L22,16 M17,8 A1.5,1.5 0 1 0 14,8 A1.5,1.5 0 1 0 17,8");
    private static readonly Geometry Camera = Icon("M3,7 H7 L9,4 H15 L17,7 H21 V20 H3 Z M17,13 A5,5 0 1 0 7,13 A5,5 0 1 0 17,13");
    private static readonly Geometry Match = Icon("M2,8 V2 H8 M16,2 H22 V8 M22,16 V22 H16 M8,22 H2 V16 M7,12 H17 M12,7 V17");
    private static readonly Geometry Line = Icon("M3,19 L21,5 M5,16 L8,19 M10,11 L13,14 M15,6 L18,9");
    private static readonly Geometry Circle = Icon("M20,12 A8,8 0 1 0 4,12 A8,8 0 1 0 20,12 M12,1 V5 M12,19 V23 M1,12 H5 M19,12 H23");
    private static readonly Geometry Distance = Icon("M3,7 H21 M3,3 V11 M21,3 V11 M6,18 H18 M6,15 V21 M18,15 V21");
    private static readonly Geometry Segment = Icon("M3,3 H21 V21 H3 Z M3,12 H21 M12,3 V21 M3,21 L21,3");
    private static readonly Geometry Classify = Icon("M3,5 H6 V8 H3 Z M10,6 H21 M3,11 H6 V14 H3 Z M10,12 H21 M3,17 H6 V20 H3 Z M10,18 H21");
    private static readonly Geometry Detect = Icon("M3,3 H21 V21 H3 Z M7,7 H17 V17 H7 Z");
    private static readonly Geometry Threshold = Icon("M3,3 H21 V21 H3 Z M12,3 V21 M3,7 H12 M3,11 H12 M3,15 H12 M3,19 H12");
    private static readonly Geometry Blob = Icon("M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M9,15 H15 V21 H9 Z M10,7 H14 M7,10 L10,15 M17,10 L14,15");
    private static readonly Geometry Scale = Icon("M4,4 H20 V20 H4 Z M4,8 H9 M4,12 H7 M4,16 H9 M8,4 V9 M12,4 V7 M16,4 V9");
    private static readonly Geometry Text = Icon("M3,4 H21 M12,4 V21 M8,21 H16");
    private static readonly Geometry Fallback = Icon("M4,4 H20 V20 H4 Z M8,12 H16 M12,8 V16");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "halcon.image-read" => Image, "halcon.camera-capture" => Camera,
        "halcon.quick-match" or "halcon.position-correction" => Match,
        "halcon.straight-line-find" => Line, "halcon.circle-find" => Circle,
        "halcon.line-pair-measure" or "halcon.line-circle-measure" or "halcon.circle-pair-measure" => Distance,
        "halcon.semantic-segmentation" => Segment, "halcon.classification" => Classify,
        "halcon.object-detection" => Detect, "halcon.threshold" => Threshold,
        "halcon.blob-analysis" => Blob, "halcon.pixel-scale" or "halcon.length-to-mm" => Scale,
        "test.constant-text" => Text, _ => Fallback
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
