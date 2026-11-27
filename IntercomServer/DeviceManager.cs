namespace IntercomServer;

internal class DeviceManager
{
    private readonly Dictionary<string, Device> _devices = new();

    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    /// <summary>
    /// The device of <paramref name="deviceId"/>, added under <paramref name="topicPrefix"/>, the
    /// prefix it was found under, when it is new. A device is known by its id alone: ids are
    /// unique across prefixes.
    /// </summary>
    public Device GetOrAdd(string topicPrefix, string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            device = new Device(topicPrefix, deviceId);
            _devices.Add(deviceId, device);
        }
        return device;
    }

    public IEnumerable<Device> GetWhere(Func<Device, bool> filter)
    {
        return _devices.Values.Where(filter);
    }

    public IEnumerable<Device> GetAllOnline()
    {
        return GetWhere(p => p.State != null && p.State.Online.GetValueOrDefault());
    }

    public IEnumerable<Device> GetAllEnabled()
    {
        return GetAllOnline().Where(p => p.State!.Enabled.GetValueOrDefault());
    }

    public void Remove(Device device)
    {
        _devices.Remove(device.DeviceId);

        OnDeviceRemoved(new DeviceEventArgs(device));
    }

    protected virtual void OnDeviceRemoved(DeviceEventArgs e) => DeviceRemoved?.Invoke(this, e);
}

internal class DeviceEventArgs(Device device) : EventArgs
{
    public Device Device { get; } = device;
}
