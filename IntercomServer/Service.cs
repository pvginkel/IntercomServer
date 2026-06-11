using IntercomServer.AIAssistant;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

internal class Service(
    Server server,
    McpToolRegistry mcpToolRegistry,
    IAssistantSessionFactory assistantSessionFactory,
    AudioEndpointResolver audioEndpointResolver
) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<Service>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await server.Connect();

        if (assistantSessionFactory.IsEnabled)
        {
            try
            {
                Logger.Information(
                    "AI assistant enabled; devices will stream audio to {Endpoint} "
                        + "(override with AUDIO_HOST if this is not reachable by the devices)",
                    audioEndpointResolver.Endpoint
                );
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not resolve the audio endpoint; set AUDIO_HOST.");
            }

            await mcpToolRegistry.InitializeAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await server.DisposeAsync();
    }
}
