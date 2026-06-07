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
        var stopwatch = Stopwatch.StartNew();
        var sent = TimeSpan.Zero;
        var buffer = new byte[
            (int)(Constants.AudioFormat.BytesPerSecond * Constants.AudioChunkSize.TotalSeconds)
        ];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                if (configuration?.Loop != true)
                    return;

                stream.Position = 0;
                continue;
            }

            // If the source idled (e.g. between ChatGPT turns) the wall clock ran ahead of
            // what we have sent. Resync so we pace from now rather than bursting the backlog.
            // For a continuous stream (a ring tone) this is a no-op.
            if (sent < stopwatch.Elapsed)
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
