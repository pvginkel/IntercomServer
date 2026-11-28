using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace IntercomServer;

internal class DeviceManager
{
    // The MQTT handler adds and removes devices while the web host's threads read them.
    private readonly ConcurrentDictionary<string, Device> _devices = new();

    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    /// <summary>
    /// The device of <paramref name="deviceId"/>, added under <paramref name="topicPrefix"/>, the
    /// prefix it was found under, when it is new. A device is known by its id alone: ids are
    /// unique across prefixes.
    /// </summary>
    public Device GetOrAdd(string topicPrefix, string deviceId)
    {
        return _devices.GetOrAdd(deviceId, id => new Device(topicPrefix, id));
    }

    public bool TryGet(string deviceId, [MaybeNullWhen(false)] out Device device)
    {
        return _devices.TryGetValue(deviceId, out device);
    }

    public IEnumerable<Device> GetAll()
    {
        return _devices.Values;
    }

    public IEnumerable<Device> GetWhere(Func<Device, bool> filter)
    {
        return _devices.Values.Where(filter);
    }

    public IEnumerable<Device> GetAllOnline()
    {
        return GetWhere(p => p.State != null && p.State.Online.GetValueOrDefault());
    }

    public IEnumerable<Device> GetAllCallCapable()
    {
        return GetWhere(p => p.CanTakeCall);
    }

    public void Remove(Device device)
    {
        _devices.TryRemove(device.DeviceId, out _);

        OnDeviceRemoved(new DeviceEventArgs(device));
    }

    protected virtual void OnDeviceRemoved(DeviceEventArgs e) => DeviceRemoved?.Invoke(this, e);
}

internal class DeviceEventArgs(Device device) : EventArgs
{
    public Device Device { get; } = device;
}
