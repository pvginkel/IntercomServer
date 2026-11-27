using System.Net;
using IntercomServer.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The real server, composed as production composes it (<see cref="Program.CreateHostBuilder"/>),
/// against a broker of its own, with its own ports and data directory. The server reads only the
/// settings the harness gives it, never the process environment, so tests run side by side and
/// nothing set where the suite runs reaches them: ChatGPT is off, as no API key is given.
/// </summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = Timeouts.Observe };

    private readonly IHost _host;
    private readonly IMqttClient _homeAssistant;
    private readonly List<TestDevice> _devices = [];

    private ServerHarness(
        TestBroker broker,
        IHost host,
        int healthPort,
        string dataDirectory,
        IMqttClient homeAssistant
    )
    {
        Broker = broker;
        _host = host;
        HealthPort = healthPort;
        DataDirectory = dataDirectory;
        _homeAssistant = homeAssistant;
    }

    public TestBroker Broker { get; }

    public int HealthPort { get; }

    /// <summary>The server's <c>DATA_DIR</c>, a temporary directory removed with the harness.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// The server's composed services, for a test that drives a part the wire cannot reach: the
    /// streaming a ChatGPT reply goes through (<see cref="LivePlayback"/>).
    /// </summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// The endpoint the server points a device's microphone at for a ChatGPT conversation: the
    /// harness's <c>AUDIO_HOST</c>, and the port the server's UDP audio server bound.
    /// </summary>
    public string AudioEndpoint =>
        $"{IPAddress.Loopback}:{Services.GetRequiredService<UdpAudioServer>().LocalEndPoint.Port}";

    /// <summary>
    /// Starts a broker and the server, and returns once the server reports ready: connected to
    /// the broker and subscribed. <paramref name="settings"/> adds to, or overrides, the
    /// settings the harness gives the server, by their environment variable names.
    /// <paramref name="services"/> replaces registrations of the server's composition, for what
    /// no setting reaches: the FFmpeg the server runs. With <paramref name="awaitReady"/> false
    /// it returns once the server's start has run, ready or not, for a server pointed away from
    /// its broker.
    /// </summary>
    public static async Task<ServerHarness> StartAsync(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        bool awaitReady = true
    )
    {
        var broker = await TestBroker.StartAsync();
        var dataDirectory = Directory.CreateTempSubdirectory("intercom-server-tests-").FullName;

        try
        {
            return await StartServerAsync(broker, dataDirectory, settings, services, awaitReady);
        }
        catch
        {
            await broker.DisposeAsync();
            Directory.Delete(dataDirectory, recursive: true);
            throw;
        }
    }

    private static async Task<ServerHarness> StartServerAsync(
        TestBroker broker,
        string dataDirectory,
        IReadOnlyDictionary<string, string?>? settings,
        Action<IServiceCollection>? services,
        bool awaitReady
    )
    {
        var (host, healthPort) = await FreePort.StartAsync(async port =>
        {
            var serverSettings = new Dictionary<string, string?>
            {
                ["MQTT_HOST"] = IPAddress.Loopback.ToString(),
                ["MQTT_PORT"] = broker.Port.ToString(),
                ["HEALTH_PORT"] = port.ToString(),
                // Port 0: the UDP audio server binds whichever port is free.
                ["AUDIO_PORT"] = "0",
                ["AUDIO_HOST"] = IPAddress.Loopback.ToString(),
                ["DATA_DIR"] = dataDirectory,
            };
            foreach (var (name, value) in settings ?? new Dictionary<string, string?>())
                serverSettings[name] = value;

            var builder = Program.CreateHostBuilder(name => serverSettings.GetValueOrDefault(name));
            if (services != null)
                builder.ConfigureServices(services);

            var host = builder.Build();
            try
            {
                using var timeout = new CancellationTokenSource(Timeouts.Observe);
                await host.StartAsync(timeout.Token);
            }
            catch
            {
                // A start that failed after the health endpoint started leaves it listening.
                await host.StopAsync();
                await ((IAsyncDisposable)host).DisposeAsync();
                throw;
            }

            return (host, port);
        });

        try
        {
            if (awaitReady)
                await WaitUntilReady(healthPort);

            var homeAssistant = await broker.ConnectAsync("home-assistant");

            return new ServerHarness(broker, host, healthPort, dataDirectory, homeAssistant);
        }
        catch
        {
            await host.StopAsync();
            await ((IAsyncDisposable)host).DisposeAsync();
            throw;
        }
    }

    /// <summary>Connects a device under <paramref name="prefix"/>; it publishes nothing until the test says so.</summary>
    public async Task<TestDevice> AddDeviceAsync(
        string deviceId,
        string prefix = TestDevice.IntercomPrefix
    )
    {
        var device = await TestDevice.ConnectAsync(Broker, prefix, deviceId);
        _devices.Add(device);
        return device;
    }

    /// <summary>
    /// Publishes as Home Assistant does, at QoS 1, so the server receives it after everything
    /// published before it (see <see cref="TestDevice.PublishAsync"/>).
    /// </summary>
    public async Task PublishAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await _homeAssistant.PublishAsync(message, timeout.Token);
    }

    public Task RingDoorbellAsync() => PublishAsync("intercom/server/set/ring_doorbell", "true");

    // A command topic the server neither reads nor publishes on.
    private const string MarkerSubtopic = "set/test_marker";

    /// <summary>
    /// Asserts the server sent <paramref name="device"/> no command, beyond what the test took
    /// from it, before a message the test has since received from the server: a marker published
    /// to the device now is its next command. The broker queues the messages to a client in the
    /// order it handles them, and it handles the server's in the order the server sends them.
    /// </summary>
    public async Task AssertSentNoCommandAsync(TestDevice device)
    {
        await PublishAsync($"{device.Prefix}/{device.DeviceId}/{MarkerSubtopic}", "marker");

        var command = await device.NextCommandAsync();
        Assert.True(
            command.Subtopic == MarkerSubtopic,
            $"Device {device.DeviceId} was sent {command.Subtopic} {command.Payload}."
        );
    }

    /// <summary>Asks the server's health endpoint, as Kubernetes' probes do.</summary>
    public async Task<(HttpStatusCode Status, string Body)> GetHealthAsync(string path)
    {
        using var response = await Http.GetAsync($"http://{IPAddress.Loopback}:{HealthPort}{path}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task WaitUntilReady(int healthPort)
    {
        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        var url = $"http://{IPAddress.Loopback}:{healthPort}/readyz";

        try
        {
            while (true)
            {
                using (var response = await Http.GetAsync(url, timeout.Token))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Waited {Timeouts.Observe.TotalSeconds:0} s for the server to report ready on {url}."
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices)
            await device.DisposeAsync();

        await _homeAssistant.DisconnectAsync();
        _homeAssistant.Dispose();

        await _host.StopAsync();
        await ((IAsyncDisposable)_host).DisposeAsync();

        await Broker.DisposeAsync();

        Directory.Delete(DataDirectory, recursive: true);
    }
}
