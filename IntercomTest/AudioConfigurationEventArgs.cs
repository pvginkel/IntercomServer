using IntercomServer.Utils;

namespace IntercomTest;

internal class AudioConfigurationEventArgs(AudioConfiguration audioConfig) : EventArgs
{
    public AudioConfiguration AudioConfig { get; } = audioConfig;
}
