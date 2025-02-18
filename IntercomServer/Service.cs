using Microsoft.Extensions.Hosting;

namespace IntercomServer;

internal class Service(Server server) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await server.Connect();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await server.DisposeAsync();
    }
}
