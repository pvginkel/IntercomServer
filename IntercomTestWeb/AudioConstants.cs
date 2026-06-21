using IntercomServer.Utils.Audio;

namespace IntercomTestWeb;

// Ported verbatim from IntercomTest.Constants. The audio format/timing are part of the device
// contract (a simulated device advertises these formats in its configuration), so they are kept
// even though Phase A does not move audio yet.
public static class AudioConstants
{
    public static readonly AudioFormat AudioFormat = new(AudioChannelLayout.Mono, 16000, 16);
    public static readonly TimeSpan BufferInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan AudioChunkSize = TimeSpan.FromMilliseconds(20);
}
