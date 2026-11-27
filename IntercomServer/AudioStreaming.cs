using System.Diagnostics;
using System.Net;
using IntercomServer.Utils.Audio;

namespace IntercomServer;

internal static class AudioStreaming
{
    /// <summary>
    /// Streams PCM audio in <paramref name="format"/> from <paramref name="stream"/> to the device
    /// <paramref name="deviceId"/> at <paramref name="endpoint"/>, in chunks of
    /// <see cref="Constants.AudioChunkSize"/>, paced to real time so the device's (small) jitter
    /// buffer is not overrun. This is the shared pacing used by both ring/doorbell playback and
    /// ChatGPT conversation audio.
    /// </summary>
    public static async Task PlayAsync(
        AudioSender sender,
        string deviceId,
        IPEndPoint endpoint,
        AudioFormat format,
        Stream stream,
        PlaybackConfiguration? configuration,
        CancellationToken cancellationToken
    )
    {
        // Only treat the source as having idled (and resync the clock) when a read actually
        // blocked for longer than this. Normal timer jitter on a buffered source stays well
        // under it, so the pacing keeps self-correcting; a continuous ring tone never waits.
        var idleThreshold = TimeSpan.FromMilliseconds(60);

        var stopwatch = Stopwatch.StartNew();
        var sent = TimeSpan.Zero;
        // A whole number of frames, so no sample straddles two chunks: 20 ms is 220.5 frames at
        // 11025 Hz.
        var frames = format.SampleRate * (int)Constants.AudioChunkSize.TotalMilliseconds / 1000;
        var buffer = new byte[frames * format.ChannelCount * (format.BitRate / 8)];

        while (!cancellationToken.IsCancellationRequested)
        {
            var beforeRead = stopwatch.Elapsed;
            var read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                if (configuration?.Loop != true)
                    return;

                stream.Position = 0;
                continue;
            }

            // If reading blocked waiting for data, the source starved — a genuine idle gap
            // (e.g. between ChatGPT turns). Resync to now so we resume real-time pacing
            // rather than bursting to "catch up". This does NOT fire on ordinary Task.Delay
            // overshoot, so continuous playback still self-corrects for timer jitter.
            if (stopwatch.Elapsed - beforeRead > idleThreshold)
                sent = stopwatch.Elapsed;

            sender.Send(deviceId, endpoint, buffer.AsSpan(0, read));

            sent += TimeSpan.FromSeconds((double)read / format.BytesPerSecond);

            if (configuration?.Duration != null && sent > configuration.Duration.Value)
                return;

            var delay = sent - stopwatch.Elapsed;
            if (delay.Ticks > 0)
                await Task.Delay(delay, cancellationToken);
        }
    }
}
