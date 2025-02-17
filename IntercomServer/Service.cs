using Microsoft.Extensions.Hosting;

namespace IntercomServer;

internal class Service : IHostedService
{
    private Server? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _server = new Server(
            new ServerConfiguration
            {
                Host = Environment.GetEnvironmentVariable("MQTT_HOST"),
                Port = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MQTT_PORT"))
                    ? null
                    : int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT")!),
                Username = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
                Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD")
            }
        );

        await _server.Connect();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
