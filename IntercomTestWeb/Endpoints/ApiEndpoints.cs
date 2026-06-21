using IntercomServer.Utils;
using IntercomTestWeb.Models;
using IntercomTestWeb.Services;
using Serilog;

namespace IntercomTestWeb.Endpoints;

// REST command surface (§6.1). Every handler is a thin pass-through to an MQTT publish or a service
// call; there is no shadow state.
public static class ApiEndpoints
{
    private static readonly ILogger Logger = Log.ForContext(typeof(ApiEndpoints));

    public static void MapApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet(
            "/devices",
            (DeviceRegistry registry, SimDeviceManager sim, SettingsStore settings) =>
                new DevicesSnapshot(registry.Snapshot(), sim.Snapshot(), settings.AutoAccept)
        );

        // ---- Real devices ----
        api.MapPost(
            "/devices/{id}/volume",
            (string id, VolumeRequest req, MqttControlService mqtt) =>
                Run(() => mqtt.SetVolumeAsync(id, req.Volume))
        );

        api.MapPost(
            "/devices/{id}/enabled",
            (string id, EnabledRequest req, MqttControlService mqtt) =>
                Run(() => mqtt.SetEnabledAsync(id, req.Enabled))
        );

        api.MapPost(
            "/devices/{id}/identify",
            (string id, MqttControlService mqtt) => Run(() => mqtt.IdentifyAsync(id))
        );

        api.MapPost(
            "/devices/{id}/restart",
            (string id, MqttControlService mqtt) => Run(() => mqtt.RestartAsync(id))
        );

        api.MapPost(
            "/devices/{id}/audio-config",
            (string id, AudioConfiguration config, MqttControlService mqtt) =>
                Run(() => mqtt.SetAudioConfigAsync(id, config))
        );

        api.MapDelete(
            "/devices/{id}",
            (string id, MqttControlService mqtt) => Run(() => mqtt.RemoveRealDeviceAsync(id))
        );

        // ---- Simulated devices ----
        api.MapPost(
            "/sim-devices",
            async (SimDeviceManager sim) =>
            {
                try
                {
                    return Results.Ok(await sim.AddAsync());
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to add a simulated device");
                    return Problem(ex);
                }
            }
        );

        api.MapDelete(
            "/sim-devices/{id}",
            (string id, SimDeviceManager sim) => Run(() => sim.RemoveAsync(id))
        );

        api.MapPost(
            "/sim-devices/{id}/action",
            (string id, ActionRequest req, SimDeviceManager sim) =>
                Run(() => sim.SendActionAsync(id, req.Action))
        );

        // ---- Server-level ----
        api.MapPost(
            "/server/doorbell",
            (MqttControlService mqtt) => Run(() => mqtt.RingDoorbellAsync())
        );

        api.MapPost(
            "/server/auto-accept",
            (AutoAcceptRequest req, MqttControlService mqtt) =>
                Run(() => mqtt.SetAutoAcceptAsync(req.Enabled))
        );
    }

    // Runs a command and maps any failure (e.g. the broker being down) to a 502 rather than a 500.
    private static async Task<IResult> Run(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Command failed");
            return Problem(ex);
        }
    }

    private static IResult Problem(Exception ex) =>
        Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
}
