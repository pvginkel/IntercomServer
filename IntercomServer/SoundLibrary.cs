using System.Collections.Concurrent;
using System.Globalization;
using IntercomServer.Utils.Audio;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

/// <summary>
/// The built-in sounds, decoded by FFmpeg into each format a device is sent them in, once per
/// format. At the server's start it decodes them in <see cref="Constants.AudioFormat"/>, the
/// format every intercom plays, and a server whose FFmpeg cannot decode them stops there: nothing
/// else in a start runs FFmpeg, so it would otherwise read as healthy until the first doorbell.
/// </summary>
internal class SoundLibrary(AudioDecoder decoder) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<SoundLibrary>();

    private readonly ConcurrentDictionary<(string FileName, AudioFormat Format), Task<byte[]>> _sounds =
        new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var lengths = new List<string>();

        foreach (var fileName in Constants.AudioFiles.All)
        {
            var pcm = await DecodeAsync(fileName, Constants.AudioFormat, cancellationToken);
            _sounds[(fileName, Constants.AudioFormat)] = Task.FromResult(pcm);

            lengths.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{fileName} {(double)pcm.Length / Constants.AudioFormat.BytesPerSecond:0.00} s"
                )
            );
        }

        Logger.Information(
            "Decoded the built-in sounds with FFmpeg: {Sounds}",
            string.Join(", ", lengths)
        );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// A stream of its own over the PCM of the built-in sound <paramref name="fileName"/> in
    /// <paramref name="format"/>, which FFmpeg decodes the first time it is asked for.
    /// </summary>
    public async Task<Stream> OpenAsync(
        string fileName,
        AudioFormat format,
        CancellationToken cancellationToken
    )
    {
        var key = (fileName, format);
        // One decode serves every playback that asks for it, so a playback that is cancelled
        // stops waiting for it and leaves it running.
        var decoding = _sounds.GetOrAdd(key, _ => DecodeAsync(fileName, format, CancellationToken.None));

        try
        {
            return new MemoryStream(await decoding.WaitAsync(cancellationToken), writable: false);
        }
        catch when (decoding.IsFaulted)
        {
            // The next playback decodes it again, rather than failing on this decode's error.
            _sounds.TryRemove(KeyValuePair.Create(key, decoding));
            throw;
        }
    }

    private async Task<byte[]> DecodeAsync(
        string fileName,
        AudioFormat format,
        CancellationToken cancellationToken
    )
    {
        await using var stream = GetType()
            .Assembly.GetManifestResourceStream($"{GetType().Namespace}.AudioFiles.{fileName}")!;

        return await decoder.DecodeAsync(fileName, stream, format, cancellationToken);
    }
}
