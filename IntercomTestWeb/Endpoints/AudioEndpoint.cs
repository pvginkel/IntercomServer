using IntercomTestWeb.Services;
using Serilog;

namespace IntercomTestWeb.Endpoints;

// The binary audio WebSocket (§6.3). One connection per simulated device carries mic frames up and
// speaker frames down, each a 4-byte big-endian sequence index + PCM16LE mono 16 kHz (D6). The socket
// is handed straight to the device's AudioBridge, which owns the framing, mixers, and UDP relay.
public static class AudioEndpoint
{
    private static readonly ILogger Logger = Log.ForContext(typeof(AudioEndpoint));

    public static void MapAudioWebSocket(this WebApplication app)
    {
        app.Map(
            "/ws/audio/{simDeviceId}",
            async (HttpContext context, string simDeviceId, SimDeviceManager sim) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var bridge = sim.GetAudioBridge(simDeviceId);
                if (bridge is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();

                try
                {
                    await bridge.RunBrowserConnection(socket, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Audio WebSocket for {Device} ended with an error", simDeviceId);
                }
            }
        );
    }
}
