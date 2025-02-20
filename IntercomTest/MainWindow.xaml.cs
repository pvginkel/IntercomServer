using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Serilog;

namespace IntercomTest;

public partial class MainWindow
{
    private static readonly ILogger Logger = Log.ForContext<MainWindow>();

    private readonly ServerConfiguration _configuration;

    public MainWindow()
    {
        InitializeComponent();

        _configuration = new ServerConfiguration
        {
            Host = Environment.GetEnvironmentVariable("MQTT_HOST"),
            Port = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MQTT_PORT"))
                ? null
                : int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT")!),
            Username = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
            Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD")
        };

        LoadDevices();
    }

    private void LoadDevices()
    {
        var fileName = Path.Combine(App.BasePath, "Devices.json");
        if (!File.Exists(fileName))
            return;

        using var stream = File.OpenRead(fileName);

        var configurations = JsonSerializer.Deserialize<List<IntercomClientConfiguration>>(stream)!;

        foreach (var configuration in configurations)
        {
            AddIntercomClientControl(configuration);
        }
    }

    private void AddIntercomClientControl(IntercomClientConfiguration configuration)
    {
        var intercomClientControl = new IntercomClientControl(configuration, _configuration)
        {
            Margin = new Thickness(3)
        };

        intercomClientControl.RemoveClicked += async (_, _) =>
        {
            try
            {
                await intercomClientControl.IntercomClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to close client");
            }

            _devices.Children.Remove(intercomClientControl);

            SaveDeviceConfiguration();
        };

        _devices.Children.Add(intercomClientControl);
    }

    private void _addDevice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        AddIntercomClientControl(new IntercomClientConfiguration(GenerateDeviceId(), null, null));

        SaveDeviceConfiguration();
    }

    private string GenerateDeviceId()
    {
        var sb = new StringBuilder("0x");
        var random = new Random();

        for (int i = 0; i < 8; i++)
        {
            sb.Append($"{random.Next(256):x2}");
        }

        return sb.ToString();
    }

    private void SaveDeviceConfiguration()
    {
        using var stream = File.Create(Path.Combine(App.BasePath, "Devices.json"));

        var intercomClientConfigurations = _devices
            .Children.Cast<IntercomClientControl>()
            .Select(p => p.GetConfiguration())
            .ToList();

        JsonSerializer.Serialize(stream, intercomClientConfigurations);
    }

    private void BaseWindow_Closed(object sender, EventArgs e)
    {
        SaveDeviceConfiguration();
    }
}
