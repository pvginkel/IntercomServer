using System.Globalization;
using IntercomServer.Utils.Audio;

namespace IntercomServer.Tests.Harness;

/// <summary>Where an occasional player's window lies on the local clock, against the test's own time.</summary>
internal enum OccasionalWindow
{
    /// <summary>It holds now and for the rest of the test.</summary>
    Holding,

    /// <summary>It starts minutes from now.</summary>
    Later,

    /// <summary>It ended this minute.</summary>
    Ended,
}

/// <summary>
/// The occasional player, driven on the real clock through its settings: a wait of a second, and
/// a window set around the test's own local time, the clock the server reads it on.
/// </summary>
internal static class OccasionalSounds
{
    /// <summary>
    /// The settings of a player whose moments come a second apart and whose window lies as
    /// <paramref name="window"/> says. A window lies within one day, so a test in the last
    /// minutes before midnight, or the first after it, first waits for 00:01.
    /// </summary>
    public static async Task<Dictionary<string, string?>> SettingsAsync(OccasionalWindow window = OccasionalWindow.Holding)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var turn = new TimeOnly(0, 1);
        if (now >= new TimeOnly(23, 56) || now < turn)
        {
            await Task.Delay(turn - now + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            now = TimeOnly.FromDateTime(DateTime.Now);
        }

        var minute = new TimeOnly(now.Hour, now.Minute);
        var (start, end) = window switch
        {
            OccasionalWindow.Holding => (new TimeOnly(0, 0), new TimeOnly(23, 59)),
            OccasionalWindow.Later => (minute.AddMinutes(2), minute.AddMinutes(3)),
            _ => (minute.AddMinutes(-1), minute),
        };

        return new Dictionary<string, string?>
        {
            ["OCCASIONAL_WINDOW_START"] = TimeOfDay(start),
            ["OCCASIONAL_WINDOW_END"] = TimeOfDay(end),
            ["OCCASIONAL_WAIT_MIN_SECONDS"] = "1",
            ["OCCASIONAL_WAIT_MAX_SECONDS"] = "1",
        };
    }

    public static string TimeOfDay(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Sets the track <paramref name="name"/> to Occasional.</summary>
    public static async Task SetOccasionalAsync(this ServerHarness harness, string name)
    {
        var result = await harness.TrackStore().UpdateAsync(name, null, TrackState.Occasional, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Track);
    }

    /// <summary>
    /// Uploads a track of white noise <paramref name="length"/> long for each of
    /// <paramref name="names"/>, seeded 1, 2 and on in their order, and returns each, by name, as
    /// the real FFmpeg decodes it into <paramref name="format"/>.
    /// </summary>
    public static async Task<Dictionary<string, short[]>> UploadNoiseAsync(
        this ServerHarness harness,
        AudioFormat format,
        TimeSpan length,
        params string[] names
    )
    {
        var tracks = new Dictionary<string, short[]>();
        for (var i = 0; i < names.Length; i++)
        {
            var noise = await Tracks.WhiteNoiseAsync("wav", length, seed: i + 1);
            await harness.UploadAsync(names[i], noise);
            tracks[names[i]] = await AmbientAudio.DecodedAsync(noise, format);
        }
        return tracks;
    }

    /// <summary>
    /// The packets that arrive at <paramref name="device"/> before the next sound of one of
    /// <paramref name="tracks"/>, and that sound's: from the packet that begins it until none
    /// arrives for <see cref="AmbientAudio.Quiet"/>.
    /// </summary>
    public static async Task<(List<AudioPacket> Before, List<AudioPacket> Sound)> UntilNextSoundAsync(
        TestDevice device,
        IReadOnlyDictionary<string, short[]> tracks
    )
    {
        var before = new List<AudioPacket>();
        while (true)
        {
            var packet = await device.NextPacketAsync();
            if (tracks.Values.Any(track => AmbientAudio.Begins(packet, track)))
                return (before, [packet, .. await AmbientAudio.RestAsync(device)]);
            before.Add(packet);
        }
    }

    /// <summary>
    /// Asserts <paramref name="sound"/> is one sound of <paramref name="track"/> from its start,
    /// ended before half of it had played: a sound that gave way.
    /// </summary>
    public static void AssertCutShort(IReadOnlyList<AudioPacket> sound, short[] track)
    {
        var samples = AmbientAudio.Samples(sound);
        Assert.True(samples.Length < track.Length / 2, $"The sound sent {samples.Length} of the track's {track.Length} samples.");
        AmbientAudio.AssertOnePlayback(samples, track, fade: 0, fadedOut: false);
    }

    /// <summary>Asserts <paramref name="sound"/> is one whole sound of <paramref name="track"/>, from its start to its end.</summary>
    public static void AssertWhole(IReadOnlyList<AudioPacket> sound, short[] track)
    {
        var samples = AmbientAudio.Samples(sound);
        Assert.Equal(track.Length, samples.Length);
        AmbientAudio.AssertOnePlayback(samples, track, fade: 0, fadedOut: false);
    }

    /// <summary>
    /// The packets of the next sound to <paramref name="device"/>: from the next packet, waited
    /// for as long as a test waits for anything, until none arrives for
    /// <see cref="AmbientAudio.Quiet"/>, half a wait.
    /// </summary>
    public static async Task<List<AudioPacket>> NextSoundAsync(TestDevice device)
    {
        var packets = new List<AudioPacket> { await device.NextPacketAsync() };
        packets.AddRange(await AmbientAudio.RestAsync(device));
        return packets;
    }

    /// <summary>
    /// The one of <paramref name="tracks"/>, by name, whose start <paramref name="sound"/>'s first
    /// packet carries.
    /// </summary>
    public static string Which(IReadOnlyList<AudioPacket> sound, IReadOnlyDictionary<string, short[]> tracks) =>
        Assert.Single(tracks, track => AmbientAudio.Begins(sound[0], track.Value)).Key;
}
