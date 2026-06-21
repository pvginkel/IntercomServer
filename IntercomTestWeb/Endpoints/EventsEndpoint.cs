using System.Net.WebSockets;
using IntercomTestWeb.Models;
using IntercomTestWeb.Services;
using Serilog;

namespace IntercomTestWeb.Endpoints;

// The /ws/events JSON push channel (§6.2). On connect the current state is replayed to the new
// socket (server settings + every known real and simulated device), so the browser builds its store
// purely from this stream — no snapshot/stream race. Thereafter the EventHub pushes incremental
// updates as MQTT/sim-device changes arrive.
public static class EventsEndpoint
{
    private static readonly ILogger Logger = Log.ForContext(typeof(EventsEndpoint));

    public static void MapEventsWebSocket(this WebApplication app)
    {
        app.Map(
            "/ws/events",
            async (
                HttpContext context,
                EventHub hub,
                DeviceRegistry registry,
                SimDeviceManager sim,
                SettingsStore settings
            ) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var connection = hub.Register(socket);

                try
                {
                    ReplayState(connection, registry, sim, settings);
                    await ReceiveUntilClosed(socket, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Events WebSocket ended with an error");
                }
                finally
                {
                    hub.Unregister(connection);
                }
            }
        );
    }

    private static void ReplayState(
        EventConnection connection,
        DeviceRegistry registry,
        SimDeviceManager sim,
        SettingsStore settings
    )
    {
        connection.Send(new ServerSettingsMessage(settings.AutoAccept));

        foreach (var device in registry.Snapshot())
        {
            if (device.Config != null)
                connection.Send(new DeviceConfigMessage("real", device.Id, device.Config));
            if (device.State != null)
                connection.Send(new DeviceStateMessage("real", device.Id, device.State));
        }

        foreach (var device in sim.Snapshot())
        {
            connection.Send(new DeviceConfigMessage("sim", device.Id, device.Config));
            connection.Send(
                new DeviceStateMessage(
                    "sim",
                    device.Id,
                    device.State,
                    device.LedRed,
                    device.LedGreen
                )
            );
        }
    }

    // The browser never sends on this socket; just drain until it closes so we notice the
    // disconnect and unregister.
    private static async Task ReceiveUntilClosed(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    null,
                    CancellationToken.None
                );
                break;
            }
        }
    }
}
