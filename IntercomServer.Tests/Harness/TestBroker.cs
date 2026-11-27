using System.Net;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Server;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// An MQTTnet broker in the test process, on a loopback port of its own: the server, the test's
/// devices and its Home Assistant all connect to it.
/// </summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly MqttServer _server;

    private TestBroker(MqttServer server, int port)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; }

    public static Task<TestBroker> StartAsync() =>
        FreePort.StartAsync(async port =>
        {
            var options = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
                .WithDefaultEndpointPort(port)
                .Build();

            var server = new MqttServerFactory().CreateMqttServer(options);
            try
            {
                await server.StartAsync();
            }
            catch
            {
                server.Dispose();
                throw;
            }

            return new TestBroker(server, port);
        });

    /// <summary>
    /// Connects a client of the test's own, on MQTT 5 so that a device's subscription can leave
    /// out what the device itself publishes.
    /// </summary>
    public async Task<IMqttClient> ConnectAsync(string clientId)
    {
        var client = new MqttClientFactory().CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(IPAddress.Loopback.ToString(), Port)
            .WithClientId(clientId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .Build();

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await client.ConnectAsync(options, timeout.Token);

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }
}
