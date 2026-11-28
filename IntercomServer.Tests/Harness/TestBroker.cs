using System.Net;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// An MQTTnet broker in the test process, on a loopback port of its own: the server, the test's
/// devices and its Home Assistant all connect to it.
/// </summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly MqttServer _server;

    // The ids of the clients the test connected itself; the server's is none of them.
    private readonly HashSet<string> _testClients = [];

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

        lock (_testClients)
            _testClients.Add(clientId);

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await client.ConnectAsync(options, timeout.Token);

        return client;
    }

    /// <summary>
    /// Closes the server's connection from the broker's side, as a lost network does: the broker
    /// publishes the server's last will, and the server connects again on its own.
    /// </summary>
    public async Task DropServerAsync()
    {
        var clients = await _server.GetClientsAsync();
        MqttClientStatus server;
        lock (_testClients)
            server = Assert.Single(clients, client => !_testClients.Contains(client.Id));

        await server.DisconnectAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }
}
