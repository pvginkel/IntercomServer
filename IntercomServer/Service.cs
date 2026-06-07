using IntercomServer.ChatGpt;
using Microsoft.Extensions.Hosting;

namespace IntercomServer;

internal class Service(
    Server server,
    McpToolRegistry mcpToolRegistry,
    ChatGptConfiguration chatGptConfiguration
) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await server.Connect();

        if (chatGptConfiguration.IsEnabled)
            await mcpToolRegistry.InitializeAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await server.DisposeAsync();
    }
}
