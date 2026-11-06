using System.Windows;

namespace HalconVision.App;

public partial class MainWindow
{
    private MeasurementStudyWindow? _measurementStudyWindow;
    private void MeasurementStudy_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_measurementStudyWindow is { } existing) { existing.Activate(); return; }
        var window = new MeasurementStudyWindow(this, _vm);
        window.Closed += (_, _) => _measurementStudyWindow = null;
        _measurementStudyWindow = window; window.Show();
    }
}
