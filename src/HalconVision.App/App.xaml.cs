using System.Windows;

namespace HalconVision.App;

public partial class App : System.Windows.Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }
}
