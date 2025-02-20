namespace IntercomTest;

internal class DeviceStreamEventArgs(string stream) : EventArgs
{
    public string Stream { get; } = stream;
}
