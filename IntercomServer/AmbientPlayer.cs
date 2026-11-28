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
    /// The player cannot play on the device, or has nothing to play: no enabled track, or a track
    /// that does not decode or decodes to no audio.
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
/// its stop. Each chunk goes to the endpoint the device advertises now. The player remembers, in
/// memory only, the track each device's latest playback played, which a pick at random skips.
/// </summary>
internal sealed class AmbientPlayer(
    AmbientConfiguration configuration,
    TrackStore tracks,
    AudioDecoder decoder,
    AudioSender sender
) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<AmbientPlayer>();

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

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
            return _playbacks.GetValueOrDefault(device.DeviceId)?.Track;
    }

    /// <summary>
    /// Starts ambient sound on <paramref name="device"/>: the track named
    /// <paramref name="track"/>, enabled or not, or, with none, an enabled track picked at random.
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
                // A playback still fading out ends before this one's sound begins.
                playback = new Playback(device, format, track, current?.Ended.Task ?? Task.CompletedTask);
                _playbacks[device.DeviceId] = playback;
                starting = true;
            }
        }

        if (starting)
            _ = RunAsync(playback);

        return await playback.Started.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the ambient sound on <paramref name="device"/>, and returns once its playback has
    /// ended: one that is playing fades out, and a start that has not yet begun its sound is
    /// abandoned, its decode with it.
    /// </summary>
    public async Task StopAsync(Device device)
    {
        Playback? playback;
        lock (_syncRoot)
        {
            if (_playbacks.TryGetValue(device.DeviceId, out playback))
                playback.Stopping = true;
        }

        if (playback == null)
            return;

        await playback.Stop.CancelAsync();
        await playback.Ended.Task;
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
        try
        {
            if (await PrepareAsync(playback) is not { } prepared)
                return;

            await playback.PreviousEnded.WaitAsync(playback.Stop.Token);

            lock (_syncRoot)
            {
                playback.Track = prepared.Track;
                _lastPlayed[device.DeviceId] = prepared.Track;
            }

            Logger.Information(
                "Playing ambient track {Track} on device {Device} at a gain of {Gain:0.0} dB",
                prepared.Track,
                device.DeviceId,
                prepared.Gain
            );
            playback.Started.SetResult(AmbientResult.Playing(prepared.Track));

            await StreamAsync(playback, prepared.Pcm);

            Logger.Information("Ended ambient track {Track} on device {Device}", prepared.Track, device.DeviceId);
        }
        // Only a start is abandoned: the stream itself waits on nothing a stop cancels.
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
            lock (_syncRoot)
            {
                if (_playbacks.GetValueOrDefault(device.DeviceId) == playback)
                    _playbacks.Remove(device.DeviceId);
            }

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
                    : Refuse(device, AmbientRefusal.CannotPlay, "There is no enabled track to play.")
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

    private static Prepared? Refused(Playback playback, AmbientResult refused)
    {
        playback.Started.SetResult(refused);
        return null;
    }

    // Under the store's lock: an enabled track at random, but the device's last-played one
    // unless it is the only one.
    private Track? Pick(Device device, IReadOnlyList<Track> all)
    {
        string? lastPlayed;
        lock (_syncRoot)
            lastPlayed = _lastPlayed.GetValueOrDefault(device.DeviceId);

        var enabled = all.Where(track => track.Enabled).ToList();
        var candidates =
            enabled.Count > 1
                ? enabled.Where(track => !string.Equals(track.Name, lastPlayed, StringComparison.OrdinalIgnoreCase)).ToList()
                : enabled;

        return candidates.Count == 0 ? null : candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>
    /// Sends <paramref name="pcm"/>, looped, in chunks of <see cref="Constants.AudioChunkSize"/>
    /// paced to real time, each filled across the loop's seam. The level ramps up from silence
    /// over the fade, and, once a stop has come, down to silence over the fade, after which the
    /// stream ends. It ends too once the device advertises no endpoint, or another format.
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

        var position = 0; // The frame of the track the next chunk starts at.
        long sent = 0; // The frames sent.
        long? fadingFrom = null; // The frames sent when the fade-out began.
        var fadingLevel = 1.0; // The level the fade-out began at.
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
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

            for (var i = 0; i < frames; i++)
            {
                var frame = sent + i;
                var level = fadingFrom is { } since ? fadingLevel * (since + fade - frame) / fade : FadeIn(frame);
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(position * 2));
                BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 2), (short)Math.Round(sample * level));
                position = (position + 1) % trackFrames;
            }

            sender.Send(device.DeviceId, endpoint, format, chunk.AsSpan(0, frames * 2));
            sent += frames;

            var delay = TimeSpan.FromSeconds((double)sent / format.SampleRate) - stopwatch.Elapsed;
            if (delay.Ticks > 0)
                await Task.Delay(delay);
        }

        double FadeIn(long frame) => frame < fade ? (double)frame / fade : 1;
    }

    /// <summary>
    /// A device's playback, from its start until it has ended. A start that finds one playing
    /// or starting waits on its <see cref="Started"/>, and one that finds it stopping makes a new
    /// one, whose sound waits for <see cref="PreviousEnded"/>.
    /// </summary>
    private sealed class Playback(Device device, AudioFormat format, string? requested, Task previousEnded)
    {
        public Device Device { get; } = device;

        public AudioFormat Format { get; } = format;

        /// <summary>The track the start named, or null for a pick at random.</summary>
        public string? Requested { get; } = requested;

        public Task PreviousEnded { get; } = previousEnded;

        /// <summary>Under the player's lock: whether a stop has come, ahead of <see cref="Stop"/>.</summary>
        public bool Stopping { get; set; }

        /// <summary>Under the player's lock: the track, once the sound has begun.</summary>
        public string? Track { get; set; }

        /// <summary>Cancelled by a stop: it abandons the start, or fades the sound out.</summary>
        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource<AmbientResult> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A track decoded for a playback, its <paramref name="Gain"/> in dB applied to its PCM.</summary>
    private sealed record Prepared(string Track, byte[] Pcm, double Gain);
}
