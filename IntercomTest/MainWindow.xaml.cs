using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using IntercomServer.Utils;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using Serilog;

namespace IntercomTest;

public partial class MainWindow
{
    private static readonly ILogger Logger = Log.ForContext<MainWindow>();

    private readonly ServerConfiguration _configuration;
    private readonly MqttClientFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly AudioRecorderServer _audioRecorderServer = new();
    private readonly Dictionary<string, DeviceState> _deviceStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Task<string> _latestFirmwareVersion;
    private AECTestWindow? _aecTestWindow;

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

        _latestFirmwareVersion = LoadLatestFirmwareVersion();

        LoadDevices();
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

            _client.ApplicationMessageReceivedAsync += _client_ApplicationMessageReceivedAsync;

            await _client.ConnectAsync(mqttClientOptionsBuilder.Build());

            IsEnabled = true;

            await _client.PublishStringAsync(
                "intercom/server/set/auto_accept",
                _autoAccept.IsChecked.GetValueOrDefault() ? "true" : "false"
            );

            await _client.SubscribeAsync("intercom/client/+/configuration");
            await _client.SubscribeAsync("intercom/client/+/state");

            using (var key = App.BaseKey)
            {
                _autoAccept.IsChecked = key.GetValue("Auto Accept") switch
                {
                    int value => value != 0,
                    _ => false
                };
            }

#if true
            await Task.Delay(TimeSpan.FromSeconds(0.1));

            _aecTest.PerformClick();
#endif
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect");
        }
    }

    private Task _client_ApplicationMessageReceivedAsync(
        MqttApplicationMessageReceivedEventArgs arg
    )
    {
        var match = Regex.Match(
            arg.ApplicationMessage.Topic,
            "^intercom/client/([^/]+)/(configuration|state)$"
        );
        if (!match.Success)
        {
            Logger.Warning("Invalid topic {Topic}", arg.ApplicationMessage.Topic);

            return Task.CompletedTask;
        }

        var deviceId = match.Groups[1].Value;
        var action = match.Groups[2].Value;
        var payload = arg.ApplicationMessage.ConvertPayloadToString();

        Dispatcher.BeginInvoke(() =>
        {
            switch (action)
            {
                case "configuration":
                    if (payload == null)
                    {
                        RemoveRealDevice(deviceId);
                    }
                    else
                    {
                        var configuration = JsonSerializer.Deserialize<DeviceConfiguration>(
                            payload,
                            IntercomClient.JsonSerializerOptions
                        )!;

                        AddOrUpdateRealDevice(deviceId, configuration);
                    }
                    break;

                case "state":
                    if (payload != null)
                    {
                        _deviceStates[deviceId] = JsonSerializer.Deserialize<DeviceState>(
                            payload,
                            IntercomClient.JsonSerializerOptions
                        )!;
                        UpdateRealDeviceState(deviceId);
                    }
                    break;
            }
        });

        return Task.CompletedTask;
    }

    private RealDeviceControl? FindDevice(string deviceId)
    {
        return _devices
            .Children.OfType<RealDeviceControl>()
            .SingleOrDefault(p =>
                string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            );
    }

    private void AddOrUpdateRealDevice(string deviceId, DeviceConfiguration configuration)
    {
        if (
            _testDevices
                .Children.OfType<IntercomClientControl>()
                .Any(p =>
                    string.Equals(p.Device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                )
        )
            return;

        var device = FindDevice(deviceId);
        if (device == null)
        {
            device = new RealDeviceControl(
                deviceId,
                _latestFirmwareVersion.GetAwaiter().GetResult()
            )
            {
                Margin = new Thickness(3)
            };

            device.RemoveClicked += async (_, _) =>
            {
                try
                {
                    await _client.PublishBinaryAsync(
                        $"intercom/client/{deviceId}/state",
                        retain: true
                    );
                    await _client.PublishBinaryAsync(
                        $"intercom/client/{deviceId}/configuration",
                        retain: true
                    );
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to remove device");
                }
            };

            device.VolumeChanged += async (s, e) =>
            {
                try
                {
                    await _client.PublishStringAsync(
                        $"intercom/client/{deviceId}/set/volume",
                        JsonSerializer.Serialize(e, IntercomClient.JsonSerializerOptions)
                    );
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to publish volume");
                }
            };

            device.IdentifyClicked += async (s, e) =>
            {
                try
                {
                    await _client.PublishStringAsync(
                        $"intercom/client/{deviceId}/set/identify",
                        "true"
                    );
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to request identification");
                }
            };

            device.RestartClicked += async (s, e) =>
            {
                try
                {
                    await _client.PublishStringAsync(
                        $"intercom/client/{deviceId}/set/restart",
                        "true"
                    );
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to request restart");
                }
            };

            _devices.Children.Add(device);
        }

        device.SetConfiguration(configuration);
    }

    private void RemoveRealDevice(string deviceId)
    {
        var device = FindDevice(deviceId);
        if (device != null)
            _devices.Children.Remove(device);
    }

    private void UpdateRealDeviceState(string deviceId)
    {
        FindDevice(deviceId)?.SetState(_deviceStates[deviceId]);
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

            _testDevices.Children.Remove(intercomClientControl);

            SaveDeviceConfiguration();
        };

        _testDevices.Children.Add(intercomClientControl);
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

        var intercomClientConfigurations = _testDevices
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

    private void _aecTest_Click(object sender, RoutedEventArgs e)
    {
        var devices = _devices
            .Children.OfType<RealDeviceControl>()
            .Select(p => new DeviceRef(p.DeviceId, p.Configuration!))
            .ToList();

        if (_aecTestWindow == null)
        {
            _aecTestWindow = new AECTestWindow(_client, devices) { Owner = this, Icon = Icon };

            _aecTestWindow.Closed += (_, _) => _aecTestWindow = null;
        }

        _aecTestWindow.Show();
    }

    private async Task<string> LoadLatestFirmwareVersion()
    {
        await using var stream = await App.HttpClient.GetStreamAsync(
            "http://iotsupport.home/assets/intercom-ota.bin"
        );

        const int headerOffset = 0x20;
        const int versionOffset = 16;

        await stream.ReadExactlyAsync(new byte[headerOffset + versionOffset]);

        byte[] version = new byte[32];
        await stream.ReadExactlyAsync(version);

        return Encoding.ASCII.GetString(version).TrimEnd('\0', ' ', '\r', '\n');
    }
}
