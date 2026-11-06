using System.Windows;
namespace HalconVision.App;
public partial class MainWindow
{
    private RunArchiveWindow? _archiveWindow;
    private void RunArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_archiveWindow is { } existing) { existing.Activate(); return; }
        var window = new RunArchiveWindow(this, _vm);
        window.Closed += (_, _) => _archiveWindow = null;
        _archiveWindow = window; window.Show();
    }
}
