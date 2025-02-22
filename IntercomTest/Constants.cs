using IntercomServer.Utils.Audio;

namespace IntercomTest;

internal class Constants
{
    public static readonly AudioFormat AudioFormat = new(AudioChannelLayout.Mono, 16000, 16);
    public static readonly TimeSpan BufferInterval = TimeSpan.FromMilliseconds(100);
}
