using System.Windows;
using Serilog;

namespace IntercomTest;

public partial class App
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
    }
}
