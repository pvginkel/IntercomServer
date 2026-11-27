using IntercomServer.Utils.Audio;
using Microsoft.Extensions.DependencyInjection;

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

    public static readonly TimeSpan RejectedLength = TimeSpan.FromSeconds(1.646);

    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(0.5);

    /// <summary>Bytes a second at mono 16 kHz 16-bit, the format every device in production plays.</summary>
    public const int BytesPerSecond = 32000;

    public static int Bytes(TimeSpan length) => (int)(length.TotalSeconds * BytesPerSecond);

    /// <summary>
    /// The PCM the server plays of the built-in sound <paramref name="fileName"/> in
    /// <paramref name="format"/>, as its <see cref="SoundLibrary"/> decoded it.
    /// </summary>
    public static async Task<byte[]> DecodedAsync(ServerHarness harness, string fileName, AudioFormat format)
    {
        await using var stream = await harness
            .Services.GetRequiredService<SoundLibrary>()
            .OpenAsync(fileName, format, TestContext.Current.CancellationToken);
        var pcm = new MemoryStream();
        await stream.CopyToAsync(pcm, TestContext.Current.CancellationToken);
        return pcm.ToArray();
    }
}
