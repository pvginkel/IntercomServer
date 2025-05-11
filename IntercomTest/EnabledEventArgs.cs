namespace IntercomTest;

internal class EnabledEventArgs(bool isEnabled) : EventArgs
{
    public bool IsEnabled { get; } = isEnabled;
}
