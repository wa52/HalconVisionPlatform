using System.Windows;
using System.Windows.Controls;
using System.IO;

namespace HalconVision.App;

public partial class App : System.Windows.Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(1500));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(0));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(15000));
        ToolTipService.ShowOnDisabledProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(true));
        var args = e.Args.Concat(Environment.GetCommandLineArgs().Skip(1));
        var projectPath = ResolveProjectArgument(args);
        var window = new MainWindow(projectPath);
        MainWindow = window;
        window.Show();
    }

    private static string? ResolveProjectArgument(IEnumerable<string> args)
    {
        var values = args.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index].Trim().Trim('"');
            if (string.Equals(value, "--project", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < values.Length) value = values[++index].Trim().Trim('"');
                else continue;
            }
            else if (value.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                value = value[10..].Trim().Trim('"');

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) value = uri.LocalPath;
            if (string.Equals(Path.GetExtension(value), ".hvproj", StringComparison.OrdinalIgnoreCase) && File.Exists(value))
                return Path.GetFullPath(value);
        }
        return null;
    }
}
