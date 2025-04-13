using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using MQTTnet;
using Serilog;

namespace IntercomTest;

public partial class MainWindow
{
    private static readonly ILogger Logger = Log.ForContext<MainWindow>();

    private readonly ServerConfiguration _configuration;
    private readonly MqttClientFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly AudioRecorderServer _audioRecorderServer = new();

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

        _client = _factory.CreateMqttClient();

        IsEnabled = false;

        LoadDevices();

        using (var key = App.BaseKey)
        {
            _autoAccept.IsChecked = key.GetValue("Auto Accept") switch
            {
                int value => value != 0,
                _ => false
            };
        }
    }

    private async void BaseWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(_configuration.Host, _configuration.Port)
                .WithWillRetain();

            if (!string.IsNullOrEmpty(_configuration.Username))
            {
                mqttClientOptionsBuilder = mqttClientOptionsBuilder.WithCredentials(
                    _configuration.Username,
                    _configuration.Password
                );
            }

            await _client.ConnectAsync(mqttClientOptionsBuilder.Build());

            IsEnabled = true;

            await _client.PublishStringAsync(
                "intercom/server/set/auto_accept",
                _autoAccept.IsChecked.GetValueOrDefault() ? "true" : "false"
            );
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect");
        }
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

    private async void _doorbell_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _client.PublishStringAsync("intercom/server/set/ring_doorbell", "true");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to ring doorbell");
        }
    }

    private async void _autoAccept_Checked(object sender, RoutedEventArgs e)
    {
        var autoAccept = _autoAccept.IsChecked.GetValueOrDefault();

        using (var key = App.BaseKey)
        {
            key.SetValue("Auto Accept", autoAccept ? 1 : 0);
        }

        try
        {
            await _client.PublishStringAsync(
                "intercom/server/set/auto_accept",
                autoAccept ? "true" : "false"
            );
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to ring doorbell");
        }
    }
}
