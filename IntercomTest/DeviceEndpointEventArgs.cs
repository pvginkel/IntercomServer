namespace IntercomTest;

internal class DeviceEndpointEventArgs(string stream) : EventArgs
{
    public string Stream { get; } = stream;
}
