using System.Net;
using Serilog;

namespace IntercomServer;

/// <summary>
/// Plays the built-in sounds, a playback of its own on each device: a sound started on some
/// devices replaces what those devices were playing, and nothing else.
/// </summary>
internal class PlaybackManager(AudioSender sender, SoundLibrary sounds)
{
    private static readonly ILogger Logger = Log.ForContext<PlaybackManager>();

    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, CancellationTokenSource> _playbacks = new();

    /// <summary>
    /// Plays <paramref name="fileName"/> on each of <paramref name="devices"/>. A device without an
    /// endpoint the server can send to is logged and skipped, and the others play.
    /// </summary>
    public void StartPlayback(
        IEnumerable<Device> devices,
        string fileName,
        PlaybackConfiguration? configuration = null,
        CancellationToken token = default
    )
    {
        foreach (var device in devices)
        {
            if (
                device.Configuration?.Endpoint is not { } advertised
                || !IPEndPoint.TryParse(advertised, out var endpoint)
            )
            {
                Logger.Warning(
                    "Device {Device} has no audio endpoint the server can send to, so it does not play {Sound}",
                    device.DeviceId,
                    fileName
                );
                continue;
            }

            var playback = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationTokenSource? replaced;

            lock (_syncRoot)
            {
                _playbacks.Remove(device.DeviceId, out replaced);
                _playbacks.Add(device.DeviceId, playback);
            }

            replaced?.Cancel();

            RunPlayback(
                device.DeviceId,
                endpoint,
                sounds.Open(fileName),
                configuration,
                playback.Token
            );
        }
    }

    private async void RunPlayback(
        string deviceId,
        IPEndPoint endpoint,
        Stream stream,
        PlaybackConfiguration? configuration,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await AudioStreaming.PlayAsync(
                sender,
                deviceId,
                endpoint,
                stream,
                configuration,
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Playback failed for device {Device}", deviceId);
        }
    }
}

internal record PlaybackConfiguration(bool Loop = false, TimeSpan? Duration = null);
