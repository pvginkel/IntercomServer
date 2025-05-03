using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using Serilog;

namespace IntercomTest;

public partial class App
{
    public static string BasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Intercom Test"
    );

    public static RegistryKey BaseKey =>
        Registry.CurrentUser.CreateSubKey("Webathome\\Intercom Test");

    public static readonly HttpClient HttpClient = new();

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
