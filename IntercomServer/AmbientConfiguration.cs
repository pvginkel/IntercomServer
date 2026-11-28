namespace IntercomServer;

/// <summary>
/// Which devices the ambient player plays on, how its sound is levelled, faded and paced, and how
/// long it plays at most. Populated from environment variables in <c>Program.cs</c>.
/// </summary>
internal class AmbientConfiguration
{
    // A Timer's longest due time, which the cap is armed with.
    private static readonly TimeSpan LongestCap = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The topic prefixes whose devices the player plays ambient sound on, each one of
    /// <see cref="DevicesConfiguration.TopicPrefixes"/>: the ToiletAutomation devices', so the
    /// intercoms get none. Set as a comma-separated list in <c>AMBIENT_TOPIC_PREFIXES</c>.
    /// </summary>
    public IReadOnlyList<string> TopicPrefixes { get; init; } = ["toilet_automation"];

    /// <summary>
    /// The integrated loudness, in LUFS, the player levels every track to
    /// (<c>AMBIENT_LOUDNESS_LUFS</c>): −20.
    /// </summary>
    public double Loudness { get; init; } = -20;

    /// <summary>
    /// How long a playback fades in at its start and out at its stop (<c>AMBIENT_FADE_MS</c>):
    /// half a second. Zero plays and stops with no fade.
    /// </summary>
    public TimeSpan Fade { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How far a playback's stream runs ahead of real time (<c>AMBIENT_PREBUFFER_MS</c>): half a
    /// second, which fills the device's buffer at the start. Zero paces it in real time.
    /// </summary>
    public TimeSpan Prebuffer { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a playback plays at most, counted from its sound's start, before it fades out
    /// (<c>AMBIENT_CAP_SECONDS</c>): half an hour, so a lost stop cannot play all night.
    /// </summary>
    public TimeSpan Cap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Validates the configuration at startup, throwing on a prefix the server finds no device
    /// under, a loudness that is not a number, a negative fade or prebuffer, and a cap of no
    /// time or past a timer's reach.
    /// </summary>
    public void Validate(DevicesConfiguration devices)
    {
        foreach (var prefix in TopicPrefixes)
        {
            if (!devices.TopicPrefixes.Contains(prefix))
                throw new InvalidOperationException(
                    $"AMBIENT_TOPIC_PREFIXES holds '{prefix}', which is not one of DEVICES_TOPIC_PREFIXES "
                        + $"({string.Join(", ", devices.TopicPrefixes)}): the server finds no device under it."
                );
        }

        if (!double.IsFinite(Loudness))
            throw new InvalidOperationException("AMBIENT_LOUDNESS_LUFS must be a loudness in LUFS.");

        if (Fade < TimeSpan.Zero)
            throw new InvalidOperationException("AMBIENT_FADE_MS must be 0 or more milliseconds.");

        if (Prebuffer < TimeSpan.Zero)
            throw new InvalidOperationException("AMBIENT_PREBUFFER_MS must be 0 or more milliseconds.");

        if (Cap <= TimeSpan.Zero || Cap > LongestCap)
            throw new InvalidOperationException(
                $"AMBIENT_CAP_SECONDS must be from 1 to {(long)LongestCap.TotalSeconds} seconds."
            );
    }
}
