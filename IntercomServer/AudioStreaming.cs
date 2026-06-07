using System.Diagnostics;
using System.Net;

namespace IntercomServer;

internal static class AudioStreaming
{
    /// <summary>
    /// Streams PCM audio from <paramref name="stream"/> to the given endpoints, paced to
    /// real time so the receiving devices' (small) jitter buffers are not overrun. This is
    /// the shared pacing used by both ring/doorbell playback and ChatGPT conversation audio.
    /// </summary>
    public static async Task PlayAsync(
        AudioSender sender,
        IReadOnlyList<IPEndPoint> endpoints,
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
        var buffer = new byte[
            (int)(Constants.AudioFormat.BytesPerSecond * Constants.AudioChunkSize.TotalSeconds)
        ];

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

            foreach (var endpoint in endpoints)
                sender.Send(endpoint, buffer.AsSpan(0, read));

            sent += TimeSpan.FromSeconds((double)read / Constants.AudioFormat.BytesPerSecond);

            if (configuration?.Duration != null && sent > configuration.Duration.Value)
                return;

            var delay = sent - stopwatch.Elapsed;
            if (delay.Ticks > 0)
                await Task.Delay(delay, cancellationToken);
        }
    }
}
