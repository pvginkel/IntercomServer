using System.Buffers;
using System.Text.RegularExpressions;
using MQTTnet;
using Serilog;

namespace IntercomServer;

internal class Server : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<Server>();
    private static readonly Regex TopicRe = new("^intercom/([^/]*)/(.*)$", RegexOptions.Compiled);

    private readonly ServerConfiguration _configuration;
    private readonly MqttClientFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, Device> _devices = new();

    public Server(ServerConfiguration configuration)
    {
        _configuration = configuration;
        _client = _factory.CreateMqttClient();
    }

    public async Task Connect()
    {
        var mqttClientOptionsBuilder = new MqttClientOptionsBuilder().WithTcpServer(
            _configuration.Host,
            _configuration.Port
        );

        if (!string.IsNullOrEmpty(_configuration.Username))
            mqttClientOptionsBuilder = mqttClientOptionsBuilder.WithCredentials(
                _configuration.Username,
                _configuration.Password
            );

        var mqttClientOptions = mqttClientOptionsBuilder.Build();

        _client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                HandleMessage(e);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to process incoming message");
            }

            return Task.CompletedTask;
        };

        await _client.ConnectAsync(mqttClientOptions);

        await Subscribe("intercom/+/status");
        await Subscribe("intercom/+/configuration");
        await Subscribe("intercom/+/set/+");
        await Subscribe("intercom/+/stream/out");

        async Task Subscribe(string topic)
        {
            var mqttSubscribeOptions = _factory
                .CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topic))
                .Build();

            await _client.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
        }
    }

    private void HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var match = TopicRe.Match(e.ApplicationMessage.Topic);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Cannot parse incoming message topic '{e.ApplicationMessage.Topic}'"
            );
        }

        var deviceId = match.Groups[1].Value;
        var topic = match.Groups[2].Value;

        lock (_syncRoot)
        {
            if (!_devices.TryGetValue(deviceId, out var device))
            {
                device = new Device(deviceId);
                _devices.Add(deviceId, device);
            }

            switch (topic)
            {
                case "configuration":
                    device.ParseConfiguration(e.ApplicationMessage.ConvertPayloadToString());
                    break;

                case "status":
                    device.Online = MatchPayload("online");
                    break;

                case "set/action":
                    HandleDeviceAction(
                        device,
                        e.ApplicationMessage.ConvertPayloadToString() switch
                        {
                            "click" => DeviceAction.Click,
                            "long_click" => DeviceAction.LongClick,
                            var action
                                => throw new InvalidOperationException(
                                    $"Unknown device action '{action}'"
                                )
                        }
                    );
                    break;

                case "set/redled":
                    device.RedLed = MatchPayload("on");
                    break;

                case "set/greenled":
                    device.GreenLed = MatchPayload("on");
                    break;

                case "set/state":
                    device.State = MatchPayload("on");
                    break;

                case "stream/out":
                    HandleDeviceStream(device, e.ApplicationMessage.Payload);
                    break;

                default:
                    Logger.Debug(
                        "Ignoring message from device {Device} on topic {Topic}",
                        deviceId,
                        topic
                    );
                    break;
            }

            bool MatchPayload(string value) =>
                e.ApplicationMessage.ConvertPayloadToString() == value;
        }
    }

    private void HandleDeviceAction(Device device, DeviceAction action)
    {
        throw new NotImplementedException();
    }

    private void HandleDeviceStream(Device device, ReadOnlySequence<byte> applicationMessagePayload)
    {
        throw new NotImplementedException();
    }

    public async ValueTask DisposeAsync()
    {
        var mqttClientDisconnectOptions = _factory.CreateClientDisconnectOptionsBuilder().Build();

        await _client.DisconnectAsync(mqttClientDisconnectOptions, CancellationToken.None);

        _client.Dispose();
    }
}
