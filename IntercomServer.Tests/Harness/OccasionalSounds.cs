using System.Globalization;

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
