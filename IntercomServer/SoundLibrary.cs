using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

/// <summary>
/// The built-in sounds, decoded by FFmpeg once, when the server starts, into the format the
/// server sends. A server whose FFmpeg cannot decode them stops at its start: nothing else in a
/// start runs FFmpeg, so it would otherwise read as healthy until the first doorbell.
/// </summary>
internal class SoundLibrary(AudioDecoder decoder) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<SoundLibrary>();

    private FrozenDictionary<string, byte[]>? _sounds;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sounds = new Dictionary<string, byte[]>();

        foreach (var fileName in Constants.AudioFiles.All)
        {
            await using var stream = GetType()
                .Assembly.GetManifestResourceStream($"{GetType().Namespace}.AudioFiles.{fileName}")!;

            sounds[fileName] = await decoder.DecodeAsync(
                fileName,
                stream,
                Constants.AudioFormat,
                cancellationToken
            );
        }

        _sounds = sounds.ToFrozenDictionary();

        Logger.Information(
            "Decoded the built-in sounds with FFmpeg: {Sounds}",
            string.Join(
                ", ",
                sounds.Select(sound =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{sound.Key} {(double)sound.Value.Length / Constants.AudioFormat.BytesPerSecond:0.00} s"
                    )
                )
            )
        );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// A stream of its own over the PCM of the built-in sound <paramref name="fileName"/>, in
    /// <see cref="Constants.AudioFormat"/>.
    /// </summary>
    public Stream Open(string fileName) => new MemoryStream(_sounds![fileName], writable: false);
}
