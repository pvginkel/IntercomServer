using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IntercomServer.Utils;
using IntercomTestWeb.Models;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomTestWeb.Services;

// Owns the set of simulated devices (was MainWindow's _testDevices list + Devices.json persistence).
// Each device gets its own MQTT identity (SimDeviceClient) and an ephemeral UDP port reserved via
// UdpAudioServer, whose endpoint it advertises in its configuration — exactly as the WPF app did, so
// the server can route audio to it in Phase B. Audio is not yet relayed in Phase A.
public sealed class SimDeviceManager(
    ServerConfiguration configuration,
    EventHub hub,
    DataPaths paths
) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<SimDeviceManager>();

    private sealed record Handle(
        SimDevice Device,
        SimDeviceClient Client,
        UdpAudioServer Udp,
        AudioBridge Bridge
    );

    private readonly ConcurrentDictionary<string, Handle> _handles =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsSimDevice(string id) => _handles.ContainsKey(id);

    // The audio bridge for a simulated device, or null if it is unknown — used by the
    // /ws/audio/{simDeviceId} endpoint to attach a browser audio connection.
    public AudioBridge? GetAudioBridge(string id) =>
        _handles.TryGetValue(id, out var handle) ? handle.Bridge : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var config in LoadConfigurations())
        {
            // Register the id synchronously (so the control client can filter it) before kicking off
            // the connect in the background — a broker that is briefly unavailable must not block
            // host startup.
            var handle = CreateHandle(config.DeviceId);

            _ = ConnectInBackground(handle);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _handles.Values)
            await DisposeHandle(handle);

        _handles.Clear();
    }

    public IReadOnlyList<SimDeviceEntry> Snapshot() =>
        _handles
            .Values.Select(h => new SimDeviceEntry(
                h.Device.DeviceId,
                h.Device.GetConfiguration(),
                h.Device.GetState(),
                h.Device.RedLed,
                h.Device.GreenLed
            ))
            .ToList();

    public async Task<SimDeviceEntry> AddAsync()
    {
        var handle = CreateHandle(GenerateDeviceId());

        try
        {
            await handle.Client.ConnectAsync();
        }
        catch
        {
            _handles.TryRemove(handle.Device.DeviceId, out _);
            await DisposeHandle(handle);
            throw;
        }

        SaveConfigurations();

        var device = handle.Device;
        hub.Broadcast(new DeviceConfigMessage("sim", device.DeviceId, device.GetConfiguration()));
        BroadcastState(device);

        return new SimDeviceEntry(
            device.DeviceId,
            device.GetConfiguration(),
            device.GetState(),
            device.RedLed,
            device.GreenLed
        );
    }

    public async Task RemoveAsync(string id)
    {
        if (!_handles.TryRemove(id, out var handle))
            return;

        try
        {
            await handle.Client.RemoveDevice();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to clear retained topics for sim device {Device}", id);
        }

        await DisposeHandle(handle);

        SaveConfigurations();

        hub.Broadcast(new DeviceRemovedMessage("sim", id));
    }

    public Task SendActionAsync(string id, string action)
    {
        if (!_handles.TryGetValue(id, out var handle))
            throw new InvalidOperationException($"Unknown simulated device '{id}'");

        var deviceAction = action switch
        {
            "click" => DeviceAction.Click,
            "long_click" => DeviceAction.LongClick,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action"),
        };

        return handle.Client.SendActionAsync(deviceAction);
    }

    private Handle CreateHandle(string deviceId)
    {
        var udp = new UdpAudioServer(0);

        var localAddress =
            NetworkUtils.GetNetworkIPAddresses().FirstOrDefault() ?? IPAddress.Loopback;

        var device = new SimDevice(
            deviceId,
            new IPEndPoint(localAddress, udp.LocalEndPoint.Port)
        );

        var client = new SimDeviceClient(device, configuration);

        device.StateChanged += (_, _) => BroadcastState(device);

        // The bridge subscribes to the UDP server's Data event for downlink audio; it stays idle until
        // a browser attaches over /ws/audio/{deviceId}.
        var bridge = new AudioBridge(device, udp);

        var handle = new Handle(device, client, udp, bridge);
        _handles[deviceId] = handle;
        return handle;
    }

    private void BroadcastState(SimDevice device) =>
        hub.Broadcast(
            new DeviceStateMessage(
                "sim",
                device.DeviceId,
                device.GetState(),
                device.RedLed,
                device.GreenLed
            )
        );

    private async Task ConnectInBackground(Handle handle)
    {
        try
        {
            await handle.Client.ConnectAsync();

            var device = handle.Device;
            hub.Broadcast(
                new DeviceConfigMessage("sim", device.DeviceId, device.GetConfiguration())
            );
            BroadcastState(device);
        }
        catch (Exception ex)
        {
            Logger.Error(
                ex,
                "Failed to connect sim device {Device} at startup",
                handle.Device.DeviceId
            );
        }
    }

    private static async Task DisposeHandle(Handle handle)
    {
        // Dispose the bridge first so it unsubscribes from the UDP server and stops its pump before
        // the socket goes away.
        await handle.Bridge.DisposeAsync();
        await handle.Client.DisposeAsync();
        handle.Udp.Dispose();
    }

    private List<IntercomClientConfiguration> LoadConfigurations()
    {
        try
        {
            if (File.Exists(paths.DevicesFile))
            {
                using var stream = File.OpenRead(paths.DevicesFile);
                return JsonSerializer.Deserialize<List<IntercomClientConfiguration>>(
                        stream,
                        IntercomJson.Options
                    ) ?? [];
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to read {Path}; starting with no sim devices", paths.DevicesFile);
        }

        return [];
    }

    private void SaveConfigurations()
    {
        try
        {
            var configurations = _handles
                .Keys.Select(id => new IntercomClientConfiguration(id, null, null))
                .ToList();

            using var stream = File.Create(paths.DevicesFile);
            JsonSerializer.Serialize(stream, configurations, IntercomJson.Options);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to write {Path}", paths.DevicesFile);
        }
    }

    private string GenerateDeviceId()
    {
        while (true)
        {
            var sb = new StringBuilder("0x");
            for (var i = 0; i < 8; i++)
                sb.Append($"{Random.Shared.Next(256):x2}");

            var id = sb.ToString();
            if (!_handles.ContainsKey(id))
                return id;
        }
    }
}
