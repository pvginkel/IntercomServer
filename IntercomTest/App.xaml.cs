using System.IO;
using System.Windows;
using Serilog;

namespace IntercomTest;

public partial class App
{
    public static string BasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Intercom Test"
    );

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        Directory.CreateDirectory(BasePath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .CreateLogger();
    }
}
