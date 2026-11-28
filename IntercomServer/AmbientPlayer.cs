using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using IntercomServer.Utils.Audio;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

/// <summary>Why the ambient player refused a start.</summary>
internal enum AmbientRefusal
{
    /// <summary>No track has the name the start gave.</summary>
    NoSuchTrack,

    /// <summary>
    /// The player cannot play on the device, or has nothing to play: no track set to Ambient, or a
    /// track that does not decode or decodes to no audio.
    /// </summary>
    CannotPlay,
}

/// <summary>
/// What a start did: the track the device plays, or, with <see cref="Track"/> null, what the
/// player refused and a sentence saying why.
/// </summary>
internal sealed record AmbientResult(string? Track, AmbientRefusal? Refusal, string? Detail)
{
    public static AmbientResult Playing(string track) => new(track, null, null);

    public static AmbientResult Refused(AmbientRefusal refusal, string detail) => new(null, refusal, detail);
}

/// <summary>
/// Plays ambient sound on the devices under <see cref="AmbientConfiguration.TopicPrefixes"/>, at
/// most one playback on each, kept by device id. A playback is one track, decoded once by FFmpeg
/// into the device's format and held in memory, so a change to the track's file changes nothing
/// it sends. It plays at the configured loudness, fades in, loops without a gap, and fades out at
/// its stop: a stop through <see cref="StopAsync(Device)"/>, the cap, the device going offline or
/// being removed, or the server's shutdown. Its stream runs the prebuffer ahead of real time. Each
/// chunk goes to the endpoint the device advertises now. While another of the server's sounds
/// plays to the device (<see cref="AudioSender.Interrupt"/>), the stream pauses: it sends nothing,
/// and resumes where it stopped, with the fade-in, once the last of them has ended. A stop during
/// a pause ends the playback with nothing more sent. The player remembers, in memory only, the
/// track each device's latest playback played, which a pick at random skips.
/// </summary>
internal sealed class AmbientPlayer(
    AmbientConfiguration configuration,
    TrackStore tracks,
    AudioDecoder decoder,
    AudioSender sender,
    DeviceManager devices
) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<AmbientPlayer>();

    // How long the stream takes, from its start, to get the prebuffer ahead of real time.
    private static readonly TimeSpan LeadSpread = TimeSpan.FromMilliseconds(100);

    // The level, in dBFS, the gain holds a track's peak at.
    private const double PeakCeiling = -1;

    // FFmpeg prints a level to a tenth of a dB, so a track's own peak lies up to this far above
    // the one its meter gives.
    private const double PeakRounding = 0.05;

    private readonly Lock _syncRoot = new();

    // Under the lock: each device's playback, by device id, from its start until it has ended;
    // and the track each device's latest playback played.
    private readonly Dictionary<string, Playback> _playbacks = new();
    private readonly Dictionary<string, string> _lastPlayed = new();

    /// <summary>
    /// Raised, with the device, as a playback's sound begins, ahead of its start's answer, and as
    /// a playback whose sound began ends, ahead of its end: what <see cref="Playing"/> answers has
    /// changed. A handler runs on the playback's own task, and must neither block nor throw.
    /// </summary>
    public event EventHandler<DeviceEventArgs>? PlayingChanged;

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        devices.DeviceRemoved += (_, e) => Stop(e.Device);
        return Task.CompletedTask;
    }

    /// <summary>Stops every playback as <see cref="StopAsync(Device)"/> does, and waits for each to end.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        List<Playback> playbacks;
        lock (_syncRoot)
        {
            playbacks = _playbacks.Values.ToList();
            foreach (var playback in playbacks)
                playback.Stopping = true;
        }

        foreach (var playback in playbacks)
            await playback.Stop.CancelAsync();
        await Task.WhenAll(playbacks.Select(playback => playback.Ended.Task)).WaitAsync(cancellationToken);
    }

    /// <summary>The track <paramref name="device"/> plays, from the moment its sound began until its playback has ended, or null.</summary>
    public string? Playing(Device device)
    {
        lock (_syncRoot)
        {
            // Of a playback and those before it, one at most has a track: its sound begins only
            // once the one before it has ended.
            for (var playback = _playbacks.GetValueOrDefault(device.DeviceId); playback != null; playback = playback.Previous)
            {
                if (playback.Track != null)
                    return playback.Track;
            }

            return null;
        }
    }

    /// <summary>
    /// Starts ambient sound on <paramref name="device"/>: the track named
    /// <paramref name="track"/>, in any state, or, with none, an Ambient track picked at random.
    /// Returns once the sound has begun or the start was refused. A device that is already
    /// playing, or starting to, plays on as it does, and the start returns what that playback's
    /// start returns.
    /// </summary>
    public async Task<AmbientResult> StartAsync(Device device, string? track, CancellationToken cancellationToken)
    {
        if (!CanPlayOn(device, out var format, out var detail))
            return Refuse(device, AmbientRefusal.CannotPlay, detail);

        Playback playback;
        var starting = false;
        lock (_syncRoot)
        {
            if (_playbacks.TryGetValue(device.DeviceId, out var current) && !current.Stopping)
                playback = current;
            else
            {
                playback = new Playback(device, format, track, current);
                _playbacks[device.DeviceId] = playback;
                starting = true;
            }
        }

        if (starting)
            _ = RunAsync(playback);

        return await playback.Started.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the ambient sound on <paramref name="device"/>, and returns once its playback, and
    /// the one it replaced, has ended: one that is playing fades out, and a start that has not yet
    /// begun its sound is abandoned, its decode with it.
    /// </summary>
    public Task StopAsync(Device device) => Stop(device)?.Ended.Task ?? Task.CompletedTask;

    /// <summary>
    /// Stops the ambient sound on <paramref name="device"/>, as <see cref="StopAsync(Device)"/>
    /// does, once it is no longer online. It does not wait for the playback's end: the MQTT
    /// handler that calls it holds up every other message while it runs.
    /// </summary>
    public void HandleDeviceState(Device device)
    {
        if (device.State?.Online != true)
            Stop(device);
    }

    // Stops the device's playback, if it has one, and returns it.
    private Playback? Stop(Device device)
    {
        Playback? playback;
        lock (_syncRoot)
            playback = _playbacks.GetValueOrDefault(device.DeviceId);

        if (playback != null)
            Stop(playback);
        return playback;
    }

    // Marks the playback stopping, so a start makes a new one, then cancels its Stop.
    private void Stop(Playback playback)
    {
        lock (_syncRoot)
            playback.Stopping = true;
        _ = playback.Stop.CancelAsync();
    }

    // Whether the player can play on the device, in the format it sends it, or why not.
    private bool CanPlayOn(
        Device device,
        [NotNullWhen(true)] out AudioFormat? format,
        [NotNullWhen(false)] out string? detail
    )
    {
        format = device.OutputFormat;
        detail = !configuration.TopicPrefixes.Contains(device.TopicPrefix)
            ? $"Device {device.DeviceId} is under '{device.TopicPrefix}', which AMBIENT_TOPIC_PREFIXES does not name."
            : device.State?.Online != true
                ? $"Device {device.DeviceId} is offline."
                : Endpoint(device) == null
                    ? $"Device {device.DeviceId} advertises no audio endpoint the server can send to."
                    : format == null
                        ? $"Device {device.DeviceId} advertises no audio format the server can produce: mono 16-bit PCM at 8 to 192 kHz."
                        : null;

        return detail == null;
    }

    private static IPEndPoint? Endpoint(Device device) =>
        device.Configuration?.Endpoint is { } advertised && IPEndPoint.TryParse(advertised, out var endpoint)
            ? endpoint
            : null;

    private static AmbientResult Refuse(Device device, AmbientRefusal refusal, string detail)
    {
        Logger.Warning("The ambient sound does not start on device {Device}: {Detail}", device.DeviceId, detail);
        return AmbientResult.Refused(refusal, detail);
    }

    private async Task RunAsync(Playback playback)
    {
        var device = playback.Device;
        var previous = playback.Previous;
        try
        {
            if (await PrepareAsync(playback) is not { } prepared)
                return;

            if (previous != null)
                await previous.Ended.Task.WaitAsync(playback.Stop.Token);

            lock (_syncRoot)
            {
                playback.Previous = null;
                playback.Track = prepared.Track;
                _lastPlayed[device.DeviceId] = prepared.Track;
            }
            PlayingChanged?.Invoke(this, new DeviceEventArgs(device));

            Logger.Information(
                "Playing ambient track {Track} on device {Device} at a gain of {Gain:0.0} dB",
                prepared.Track,
                device.DeviceId,
                prepared.Gain
            );
            playback.Started.SetResult(AmbientResult.Playing(prepared.Track));

            using (new Timer(_ => Cap(playback), null, configuration.Cap, Timeout.InfiniteTimeSpan))
                await StreamAsync(playback, prepared.Pcm);

            Logger.Information("Ended ambient track {Track} on device {Device}", prepared.Track, device.DeviceId);
        }
        // Only a start is abandoned: the stream ends at a stop without throwing.
        catch (OperationCanceledException) when (playback.Stop.IsCancellationRequested)
        {
            Logger.Information("A stop abandoned the start of the ambient sound on device {Device}", device.DeviceId);
            playback.Started.SetResult(
                AmbientResult.Refused(AmbientRefusal.CannotPlay, "The start was stopped before its sound began.")
            );
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The ambient playback on device {Device} failed", device.DeviceId);
            playback.Started.TrySetException(ex);
        }
        finally
        {
            // One that ends before its sound began keeps the device's place until the one it
            // replaced has ended, so the next start waits for that one's fade-out, and a stop for
            // its end.
            lock (_syncRoot)
                playback.Stopping = true;
            if (previous != null)
                await previous.Ended.Task;

            bool played;
            lock (_syncRoot)
            {
                played = playback.Track != null;
                playback.Track = null;
                if (_playbacks.GetValueOrDefault(device.DeviceId) == playback)
                    _playbacks.Remove(device.DeviceId);
            }

            if (played)
                PlayingChanged?.Invoke(this, new DeviceEventArgs(device));
            playback.Ended.SetResult();
        }
    }

    // The track picked, opened and decoded, its gain applied; or null, the start refused.
    private async Task<Prepared?> PrepareAsync(Playback playback)
    {
        var device = playback.Device;
        var cancellationToken = playback.Stop.Token;

        var opened = playback.Requested is { } requested
            ? await tracks.OpenAsync(requested, cancellationToken)
            : await tracks.OpenAsync(all => Pick(device, all), cancellationToken);
        if (opened is not var (track, audio))
            return Refused(
                playback,
                playback.Requested is { } name
                    ? Refuse(device, AmbientRefusal.NoSuchTrack, TrackStore.NotFound(name).Detail!)
                    : Refuse(device, AmbientRefusal.CannotPlay, "No track is set to Ambient.")
            );

        MeasuredAudio measured;
        await using (audio)
        {
            try
            {
                measured = await decoder.DecodeMeasuredAsync(track.Name, audio, playback.Format, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Refused(playback, Refuse(device, AmbientRefusal.CannotPlay, ex.Message));
            }
        }

        // Looped, no audio at all would be a stream that never waits.
        if (measured.Pcm.Length == 0)
            return Refused(
                playback,
                Refuse(device, AmbientRefusal.CannotPlay, $"The track '{track.Name}' decodes to no audio.")
            );

        // Silence measures a peak of negative infinity, so its gain is the loudness's alone.
        var gain = Math.Min(
            configuration.Loudness - measured.Loudness,
            PeakCeiling - (measured.Peak + PeakRounding)
        );
        var factor = Math.Pow(10, gain / 20);
        for (var i = 0; i < measured.Pcm.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(measured.Pcm.AsSpan(i));
            BinaryPrimitives.WriteInt16LittleEndian(measured.Pcm.AsSpan(i), (short)Math.Round(sample * factor));
        }

        return new Prepared(track.Name, measured.Pcm, gain);
    }

    private void Cap(Playback playback)
    {
        Logger.Information(
            "The ambient playback on device {Device} has played for {Cap}, its cap, and stops",
            playback.Device.DeviceId,
            configuration.Cap
        );
        Stop(playback);
    }

    private static Prepared? Refused(Playback playback, AmbientResult refused)
    {
        playback.Started.SetResult(refused);
        return null;
    }

    // Under the store's lock: an Ambient track at random, but the device's last-played one
    // unless it is the only one.
    private Track? Pick(Device device, IReadOnlyList<Track> all)
    {
        string? lastPlayed;
        lock (_syncRoot)
            lastPlayed = _lastPlayed.GetValueOrDefault(device.DeviceId);

        var ambient = all.Where(track => track.State == TrackState.Ambient).ToList();
        var candidates =
            ambient.Count > 1
                ? ambient.Where(track => !string.Equals(track.Name, lastPlayed, StringComparison.OrdinalIgnoreCase)).ToList()
                : ambient;

        return candidates.Count == 0 ? null : candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>
    /// Sends <paramref name="pcm"/>, looped, in chunks of <see cref="Constants.AudioChunkSize"/>,
    /// each filled across the loop's seam. The first chunks go faster than real time, until the
    /// device holds the prebuffer after <see cref="LeadSpread"/>, and it then stays that far
    /// ahead. What the device still holds at the start, a previous playback's lead among it,
    /// counts toward the prebuffer. The level ramps up from silence over the fade, and, once a
    /// stop has come, down to silence over the fade, after which the stream ends. A chunk the
    /// sender refuses, another sound playing to the device, pauses the stream where it is, with
    /// no fade-out. Once no other sound plays, it resumes from there as it starts: the fade-in,
    /// and a lead topped up to the prebuffer, counting what the device still holds of that sound.
    /// A stop during the pause, or a pause during the fade-out, ends the stream. It ends too once
    /// the device advertises no endpoint, or another format.
    /// </summary>
    private async Task StreamAsync(Playback playback, byte[] pcm)
    {
        var device = playback.Device;
        var format = playback.Format;
        var fade = (long)(format.SampleRate * configuration.Fade.TotalSeconds);
        // A whole number of frames, as AudioStreaming's chunks.
        var chunkFrames = format.SampleRate * (int)Constants.AudioChunkSize.TotalMilliseconds / 1000;
        var chunk = new byte[chunkFrames * 2];
        var trackFrames = pcm.Length / 2;
        var spread = LeadSpread.TotalSeconds;

        var position = 0; // The frame of the track the next chunk starts at.
        long sent = 0; // The frames sent.
        long? fadingFrom = null; // The frames sent when the fade-out began.
        var fadingLevel = 1.0; // The level the fade-out began at.
        // From the stream's start, and again from each resume: the frames sent then, the lead the
        // stream adds to what the device still holds, negative where that is more than the
        // prebuffer, and the time since.
        long resumedAt = 0;
        var lead = Lead();
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            // When the chunk that starts at the audio sent is due: that audio less the lead, which
            // grows from none at the start to the stream's whole lead at the end of the spread.
            var audio = (double)(sent - resumedAt) / format.SampleRate;
            var due = Math.Max(audio - lead, audio * spread / (Math.Max(lead, 0) + spread));
            var delay = TimeSpan.FromSeconds(due) - stopwatch.Elapsed;
            if (delay.Ticks > 0)
                await Task.Delay(delay);

            if (fadingFrom == null && playback.Stop.IsCancellationRequested)
            {
                fadingFrom = sent;
                fadingLevel = FadeIn(sent);
            }

            var frames = fadingFrom is { } from ? (int)Math.Min(chunkFrames, from + fade - sent) : chunkFrames;
            if (frames <= 0)
                return;

            if (Endpoint(device) is not { } endpoint || device.OutputFormat != format)
            {
                Logger.Warning(
                    "Device {Device} no longer advertises an audio endpoint and the format {Format} its ambient playback is in, so the playback ends",
                    device.DeviceId,
                    format
                );
                return;
            }

            var next = position;
            for (var i = 0; i < frames; i++)
            {
                var frame = sent + i;
                var level = fadingFrom is { } since ? fadingLevel * (since + fade - frame) / fade : FadeIn(frame);
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(next * 2));
                BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 2), (short)Math.Round(sample * level));
                next = (next + 1) % trackFrames;
            }

            if (!sender.SendUnlessInterrupted(device.DeviceId, endpoint, format, chunk.AsSpan(0, frames * 2)))
            {
                Logger.Information("Another sound plays on device {Device}, so its ambient sound pauses", device.DeviceId);
                await sender
                    .Uninterrupted(device.DeviceId)
                    .WaitAsync(playback.Stop.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                // A stop that came before the pause or during it ends the stream with nothing more
                // sent: the device plays the other sound, so there is nothing to fade.
                if (playback.Stop.IsCancellationRequested)
                {
                    Logger.Information("A stop ended the paused ambient sound on device {Device}", device.DeviceId);
                    return;
                }

                Logger.Information("The ambient sound on device {Device} resumes", device.DeviceId);
                resumedAt = sent;
                lead = Lead();
                stopwatch.Restart();
                continue;
            }

            position = next;
            sent += frames;
        }

        double FadeIn(long frame) => frame - resumedAt < fade ? (double)(frame - resumedAt) / fade : 1;

        double Lead() => configuration.Prebuffer.TotalSeconds - sender.Held(device.DeviceId).TotalSeconds;
    }

    /// <summary>
    /// A device's playback, from its start until it has ended. A start that finds one playing
    /// or starting waits on its <see cref="Started"/>, and one that finds it stopping makes a new
    /// one, whose sound waits for that one's <see cref="Ended"/>.
    /// </summary>
    private sealed class Playback(Device device, AudioFormat format, string? requested, Playback? previous)
    {
        public Device Device { get; } = device;

        public AudioFormat Format { get; } = format;

        /// <summary>The track the start named, or null for a pick at random.</summary>
        public string? Requested { get; } = requested;

        /// <summary>Under the player's lock: the stopping playback this one replaced, until this one's sound begins.</summary>
        public Playback? Previous { get; set; } = previous;

        /// <summary>
        /// Under the player's lock: whether a stop has come, ahead of <see cref="Stop"/>, or the
        /// playback is ending.
        /// </summary>
        public bool Stopping { get; set; }

        /// <summary>Under the player's lock: the track, from the sound's start until the playback ends.</summary>
        public string? Track { get; set; }

        /// <summary>Cancelled by a stop: it abandons the start, fades the sound out, or ends its pause.</summary>
        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource<AmbientResult> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once the playback, and the one it replaced, has ended.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A track decoded for a playback, its <paramref name="Gain"/> in dB applied to its PCM.</summary>
    private sealed record Prepared(string Track, byte[] Pcm, double Gain);
}
