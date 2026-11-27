using System.Net;
using IntercomServer.Utils.Audio;
using Serilog;

namespace IntercomServer;

/// <summary>
/// Plays the built-in sounds, a playback of its own on each device, in the format the device
/// advertises: a sound started on some devices replaces what those devices were playing, and
/// nothing else.
/// </summary>
internal class PlaybackManager(AudioSender sender, SoundLibrary sounds)
{
    private static readonly ILogger Logger = Log.ForContext<PlaybackManager>();

    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, CancellationTokenSource> _playbacks = new();

    /// <summary>
    /// Plays <paramref name="fileName"/> on each of <paramref name="devices"/>. A device without an
    /// endpoint the server can send to, or a format it can produce, is logged and skipped, and the
    /// others play.
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

            if (device.OutputFormat is not { } format)
            {
                Logger.Warning(
                    "Device {Device} advertises no audio format the server can produce ({Format}), so it does not play {Sound}",
                    device.DeviceId,
                    device.Configuration.AudioFormats?.Out,
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

            RunPlayback(device.DeviceId, endpoint, format, fileName, configuration, playback.Token);
        }
    }

    private async void RunPlayback(
        string deviceId,
        IPEndPoint endpoint,
        AudioFormat format,
        string fileName,
        PlaybackConfiguration? configuration,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var stream = await sounds.OpenAsync(fileName, format, cancellationToken);

            await AudioStreaming.PlayAsync(
                sender,
                deviceId,
                endpoint,
                format,
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
