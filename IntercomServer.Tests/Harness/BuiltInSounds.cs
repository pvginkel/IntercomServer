namespace IntercomServer.Tests.Harness;

/// <summary>
/// The built-in sounds' lengths, as ffprobe reads them from <c>IntercomServer/AudioFiles</c>.
/// What a decoder makes of a file differs from its length by the decoder's padding, tens of
/// milliseconds, so a test that needs a sound to have ended, or a looped one to have run past
/// its end, adds <see cref="Margin"/>.
/// </summary>
internal static class BuiltInSounds
{
    public static readonly TimeSpan DoorbellLength = TimeSpan.FromSeconds(3.144);

    public static readonly TimeSpan RingLength = TimeSpan.FromSeconds(6.264);

    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(0.5);

    /// <summary>Bytes a second at mono 16 kHz 16-bit, the format every device in production plays.</summary>
    public const int BytesPerSecond = 32000;

    public static int Bytes(TimeSpan length) => (int)(length.TotalSeconds * BytesPerSecond);
}
