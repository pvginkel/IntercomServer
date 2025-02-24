using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using MQTTnet;
using NAudio.Wave;
using Serilog;

namespace IntercomServer;

internal class PlaybackManager(IMqttClient client)
{
    private static readonly ILogger Logger = Log.ForContext<PlaybackManager>();

    private readonly Lock _syncRoot = new();
    private CancellationTokenSource? _cancellationTokenSource;

    public void CancelPlayback()
    {
        lock (_syncRoot)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = null;
        }
    }

    public void StartPlayback(
        IEnumerable<Device> devices,
        string fileName,
        PlaybackConfiguration? configuration = null,
        CancellationToken token = default
    )
    {
        lock (_syncRoot)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

            token = _cancellationTokenSource.Token;
        }

        var stream = DecodeFile(fileName);

        RunPlayback([.. devices], stream, configuration, token);
    }

    private async void RunPlayback(
        ImmutableArray<Device> devices,
        Stream stream,
        PlaybackConfiguration? configuration,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var sent = TimeSpan.Zero;
            var buffer = new byte[
                (int)(Constants.AudioFormat.BytesPerSecond * Constants.AudioChunkSize.TotalSeconds)
            ];

            stream.Position = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);

                if (read == 0)
                {
                    if (configuration?.Loop != true)
                        return;

                    stream.Position = 0;
                    continue;
                }

                foreach (var device in devices)
                {
                    await client.PublishBinaryAsync(
                        $"intercom/client/{device.DeviceId}/stream/in",
                        buffer.Take(read),
                        cancellationToken: cancellationToken
                    );
                }

                sent += TimeSpan.FromSeconds((double)read / Constants.AudioFormat.BytesPerSecond);

                if (configuration?.Duration != null && sent > configuration.Duration.Value)
                    return;

                var delay = sent - stopwatch.Elapsed;
                if (delay.Ticks > 0)
                    await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Playback failed");
        }
    }

    private Stream DecodeFile(string fileName)
    {
        using var stream = GetType()
            .Assembly.GetManifestResourceStream($"{GetType().Namespace}.AudioFiles.{fileName}");

        var result = new MemoryStream();

        using (var reader = new Mp3FileReader(stream))
        {
            var targetFormat = new WaveFormat(
                Constants.AudioFormat.SampleRate,
                Constants.AudioFormat.BitRate,
                Constants.AudioFormat.ChannelCount
            );

            using (var resampler = new MediaFoundationResampler(reader, targetFormat))
            {
                resampler.ResamplerQuality = 60;

                var buffer = new byte[resampler.WaveFormat.AverageBytesPerSecond];
                int read;

                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    result.Write(buffer, 0, read);
                }
            }
        }

        result.Position = 0;

        return result;
    }
}

internal record PlaybackConfiguration(bool Loop = false, TimeSpan? Duration = null);
