using System.Diagnostics;
using System.Net;
using IntercomServer.ChatGpt;
using IntercomServer.Utils.Audio;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

/// <summary>
/// Plays an Occasional track now and then, once, on a speaker drawn at random among the idle
/// ones: one sound at a time for the whole house. It waits a time drawn between the shortest and
/// the longest wait, then meets a moment, and draws the next wait once that moment is over. At a
/// moment inside the window, with an Occasional track, it plays the next track of its round on an
/// idle speaker, and skips the moment when no speaker is idle. A round is the Occasional tracks
/// not yet played in it, read from the store at each moment; once every one has played, a new
/// round starts, whose first is not the last one played unless it is the only one. The round is
/// held in memory only. A speaker is a device the server knows, online, advertising an endpoint
/// and a format the server can send, and not excluded; it is idle with no ambient playback, no
/// sound <see cref="AudioSender.Interrupt"/> marked, no ChatGPT conversation, and not calling,
/// ringing or in a call. The sound is the track decoded into the speaker's format and levelled as
/// the ambient sound is, sent once in real time, each chunk to the endpoint the device advertises
/// now; it ends early once the device is offline or removed, advertises no endpoint or another
/// format, or the server stops, whose shutdown waits for it. It gives way: it ends, not to resume,
/// with nothing more sent, once its speaker is idle no more, and at once at an ambient start on
/// its speaker, refused or not. Its track has had its turn in the round all the same.
/// </summary>
internal sealed class OccasionalPlayer(
    OccasionalConfiguration configuration,
    TrackStore tracks,
    AudioDecoder decoder,
    AudioSender sender,
    DeviceManager devices,
    AmbientPlayer ambient,
    ConversationManager conversations,
    StateManager state
) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<OccasionalPlayer>();

    private readonly CancellationTokenSource _stopping = new();
    private Task _moments = Task.CompletedTask;

    // Under the lock: the device the latest moment drew, and whether an ambient start has come to
    // it since. A chunk is checked and sent under it too, so none goes out once a start has come.
    private readonly Lock _syncRoot = new();
    private Device? _speaker;
    private bool _gaveWay;

    // Read and written only by the moments, one after another: the names played in this round,
    // ignoring case, as a name is unique in the store; and the last one played.
    private readonly HashSet<string> _played = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastPlayed;

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        ambient.Starting += (_, e) =>
        {
            lock (_syncRoot)
            {
                if (_speaker?.DeviceId == e.Device.DeviceId)
                    _gaveWay = true;
            }
        };
        _moments = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <summary>Ends a sound that plays, and returns once it has ended.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _moments.WaitAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await Task.Delay(NextWait(), stopping);
                try
                {
                    await MomentAsync(stopping);
                }
                catch (Exception ex) when (!stopping.IsCancellationRequested)
                {
                    Logger.Error(ex, "A moment of the occasional sound failed");
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    // A wait drawn at random from the shortest to the longest.
    private TimeSpan NextWait() =>
        configuration.ShortestWait + (configuration.LongestWait - configuration.ShortestWait) * Random.Shared.NextDouble();

    private async Task MomentAsync(CancellationToken stopping)
    {
        if (!configuration.WindowHolds(TimeOnly.FromDateTime(DateTime.Now)))
            return;

        if (await tracks.OpenAsync(Pick, stopping) is not var (track, audio))
            return;

        Speaker? speaker;
        MeasuredAudio measured;
        await using (audio)
        {
            lock (_syncRoot)
            {
                var idle = Speakers().Where(candidate => Idle(candidate.Device)).ToList();
                speaker = idle.Count == 0 ? null : idle[Random.Shared.Next(idle.Count)];
                _speaker = speaker?.Device;
                _gaveWay = false;
            }

            if (speaker == null)
            {
                Logger.Information("No speaker is idle, so this moment plays no occasional sound");
                return;
            }

            _played.Add(track.Name);
            _lastPlayed = track.Name;

            try
            {
                measured = await decoder.DecodeMeasuredAsync(track.Name, audio, speaker.Format, stopping);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warning(
                    "The occasional track {Track} does not play on device {Device}: {Detail}",
                    track.Name,
                    speaker.Device.DeviceId,
                    ex.Message
                );
                return;
            }
        }

        if (measured.Pcm.Length == 0)
        {
            Logger.Warning(
                "The occasional track {Track} does not play on device {Device}: it decodes to no audio",
                track.Name,
                speaker.Device.DeviceId
            );
            return;
        }

        var gain = Levelling.Apply(measured, configuration.Loudness);
        Logger.Information(
            "Playing occasional track {Track} on device {Device} at a gain of {Gain:0.0} dB",
            track.Name,
            speaker.Device.DeviceId,
            gain
        );
        await StreamAsync(speaker, measured.Pcm, stopping);
        Logger.Information("Ended occasional track {Track} on device {Device}", track.Name, speaker.Device.DeviceId);
    }

    // Under the store's lock: an Occasional track at random among those the round has not played,
    // after a new round's start once it has played them all; a new round's first is not the last
    // one played unless it is the only Occasional track.
    private Track? Pick(IReadOnlyList<Track> all)
    {
        var occasional = all.Where(track => track.State == TrackState.Occasional).ToList();
        var due = occasional.Where(track => !_played.Contains(track.Name)).ToList();
        if (due.Count == 0)
        {
            _played.Clear();
            due = occasional;
        }

        var candidates =
            _played.Count == 0 && due.Count > 1
                ? due.Where(track => !string.Equals(track.Name, _lastPlayed, StringComparison.OrdinalIgnoreCase)).ToList()
                : due;

        return candidates.Count == 0 ? null : candidates[Random.Shared.Next(candidates.Count)];
    }

    // Every device the server knows that is online, advertises an endpoint and a format it can
    // send, and is not excluded. An intercom's enabled bounds calls and the doorbell, not this.
    private IEnumerable<Speaker> Speakers()
    {
        foreach (var device in devices.GetAll())
        {
            if (
                device.State?.Online == true
                && Endpoint(device) != null
                && device.OutputFormat is { } format
                && !configuration.ExcludedDevices.Contains(device.DeviceId)
            )
                yield return new Speaker(device, format);
        }
    }

    // A ChatGPT conversation marks its sound only once its session with OpenAI is up, so the
    // conversation is asked too; and a call's audio goes from device to device, marked by
    // nothing the sender holds, so the call state is asked too.
    private bool Idle(Device device) =>
        !ambient.HasPlayback(device)
        && sender.Uninterrupted(device.DeviceId).IsCompleted
        && !conversations.IsChatting(device)
        && !state.TakesPartInCall(device);

    /// <summary>
    /// Sends <paramref name="pcm"/> once, in chunks of <see cref="Constants.AudioChunkSize"/>,
    /// paced in real time from the first, each to the endpoint the device advertises now. It ends
    /// early, with the rest unsent, once the device is offline or removed, advertises no endpoint
    /// or another format, is idle no more or has had an ambient start, or the server stops. Each
    /// chunk is refused once another sound <see cref="AudioSender.Interrupt"/> marks plays to the
    /// device, so none of it goes out after that sound's start.
    /// </summary>
    private async Task StreamAsync(Speaker speaker, byte[] pcm, CancellationToken stopping)
    {
        var (device, format) = speaker;
        // A whole number of frames, as AudioStreaming's chunks.
        var chunkBytes = format.SampleRate * (int)Constants.AudioChunkSize.TotalMilliseconds / 1000 * 2;
        var stopwatch = Stopwatch.StartNew();

        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var delay = TimeSpan.FromSeconds((double)offset / format.BytesPerSecond) - stopwatch.Elapsed;
            if (delay.Ticks > 0)
                await Task.Delay(delay, stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            if (stopping.IsCancellationRequested)
            {
                Logger.Information("The server stops, so the occasional sound on device {Device} ends", device.DeviceId);
                return;
            }

            if (
                device.State?.Online != true
                || !devices.TryGet(device.DeviceId, out var known)
                || known != device
            )
            {
                Logger.Warning(
                    "Device {Device} is offline or removed, so its occasional sound ends",
                    device.DeviceId
                );
                return;
            }

            if (Endpoint(device) is not { } endpoint || device.OutputFormat != format)
            {
                Logger.Warning(
                    "Device {Device} no longer advertises an audio endpoint and the format {Format} its occasional sound is in, so the sound ends",
                    device.DeviceId,
                    format
                );
                return;
            }

            bool sent;
            lock (_syncRoot)
                sent =
                    !_gaveWay
                    && Idle(device)
                    && sender.SendUnlessInterrupted(
                        device.DeviceId,
                        endpoint,
                        format,
                        pcm.AsSpan(offset, Math.Min(chunkBytes, pcm.Length - offset))
                    );

            if (!sent)
            {
                Logger.Information("Device {Device} is busy, so its occasional sound gives way", device.DeviceId);
                return;
            }
        }
    }

    private static IPEndPoint? Endpoint(Device device) =>
        device.Configuration?.Endpoint is { } advertised && IPEndPoint.TryParse(advertised, out var endpoint)
            ? endpoint
            : null;

    /// <summary>A device a sound can play on, and the format it is sent in.</summary>
    private sealed record Speaker(Device Device, AudioFormat Format);
}
