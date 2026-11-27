using System.Collections.Immutable;
using System.Net;
using Serilog;

namespace IntercomServer;

internal class PlaybackManager(AudioSender sender, SoundLibrary sounds)
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

        RunPlayback([.. devices], sounds.Open(fileName), configuration, token);
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
            var endpoints = devices
                .Select(device => IPEndPoint.Parse(device.Configuration!.Endpoint!))
                .ToList();

            await AudioStreaming.PlayAsync(
                sender,
                endpoints,
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
            Logger.Error(ex, "Playback failed");
        }
    }
}

internal record PlaybackConfiguration(bool Loop = false, TimeSpan? Duration = null);
