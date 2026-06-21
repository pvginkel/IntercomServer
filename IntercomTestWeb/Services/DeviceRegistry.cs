using System.Collections.Concurrent;
using IntercomServer.Utils;
using IntercomTestWeb.Models;

namespace IntercomTestWeb.Services;

// Live set of real (non-simulated) devices discovered on the MQTT bus, tracking the last
// configuration and state seen per device id. This is the web equivalent of MainWindow's
// RealDeviceControl list + _deviceStates dictionary.
public sealed class DeviceRegistry
{
    private sealed class Entry
    {
        public DeviceConfiguration? Config;
        public DeviceState? State;
    }

    private readonly ConcurrentDictionary<string, Entry> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    public void UpsertConfig(string id, DeviceConfiguration config) =>
        _devices.GetOrAdd(id, _ => new Entry()).Config = config;

    public void UpsertState(string id, DeviceState state) =>
        _devices.GetOrAdd(id, _ => new Entry()).State = state;

    public void Remove(string id) => _devices.TryRemove(id, out _);

    public IReadOnlyList<RealDeviceEntry> Snapshot() =>
        _devices
            .Select(kvp => new RealDeviceEntry(kvp.Key, kvp.Value.Config, kvp.Value.State))
            .ToList();
}
