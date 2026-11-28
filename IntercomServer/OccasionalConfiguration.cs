using System.Globalization;

namespace IntercomServer;

/// <summary>
/// When the occasional player plays, how long it waits between its moments, the devices it never
/// plays on, and how its sound is levelled. Populated from environment variables in
/// <c>Program.cs</c>.
/// </summary>
internal class OccasionalConfiguration
{
    // Task.Delay's longest wait, which a wait between moments is.
    private static readonly TimeSpan LongestDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The time of day, on the server's local clock, from which an occasional sound may start
    /// (<c>OCCASIONAL_WINDOW_START</c>): 08:00.
    /// </summary>
    public TimeOnly WindowStart { get; init; } = new(8, 0);

    /// <summary>
    /// The time of day from which no occasional sound starts, later than
    /// <see cref="WindowStart"/> on the same day (<c>OCCASIONAL_WINDOW_END</c>): 21:00. A sound
    /// started before it plays on past it.
    /// </summary>
    public TimeOnly WindowEnd { get; init; } = new(21, 0);

    /// <summary>The shortest wait before each moment, in whole seconds (<c>OCCASIONAL_WAIT_MIN_SECONDS</c>): 20 minutes.</summary>
    public TimeSpan ShortestWait { get; init; } = TimeSpan.FromSeconds(1200);

    /// <summary>The longest wait before each moment, in whole seconds (<c>OCCASIONAL_WAIT_MAX_SECONDS</c>): half an hour.</summary>
    public TimeSpan LongestWait { get; init; } = TimeSpan.FromSeconds(1800);

    /// <summary>
    /// The ids of the devices the player never plays on, such as a board on the operator's bench.
    /// Set as a comma-separated list in <c>OCCASIONAL_EXCLUDED_DEVICES</c>: none.
    /// </summary>
    public IReadOnlyList<string> ExcludedDevices { get; init; } = [];

    /// <summary>
    /// The integrated loudness, in LUFS, the player levels every occasional sound to
    /// (<c>OCCASIONAL_LOUDNESS_LUFS</c>): −20. A setting apart from
    /// <see cref="AmbientConfiguration.Loudness"/>, as the occasional sound plays on the
    /// intercoms too.
    /// </summary>
    public double Loudness { get; init; } = -20;

    /// <summary>Whether an occasional sound may start at <paramref name="time"/>: from the window's start, and before its end.</summary>
    public bool WindowHolds(TimeOnly time) => time >= WindowStart && time < WindowEnd;

    /// <summary>
    /// Validates the configuration at startup, throwing on a window that does not start before it
    /// ends, a shortest wait under a second, a longest wait under the shortest or past a delay's
    /// reach, an excluded entry that cannot be a device id, and a loudness that is not a number.
    /// </summary>
    public void Validate()
    {
        if (WindowStart >= WindowEnd)
            throw new InvalidOperationException(
                $"OCCASIONAL_WINDOW_START ({TimeOfDay(WindowStart)}) must be earlier than OCCASIONAL_WINDOW_END "
                    + $"({TimeOfDay(WindowEnd)}): the window lies within one day."
            );

        if (ShortestWait < TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("OCCASIONAL_WAIT_MIN_SECONDS must be 1 or more seconds.");

        if (LongestWait < ShortestWait || LongestWait > LongestDelay)
            throw new InvalidOperationException(
                $"OCCASIONAL_WAIT_MAX_SECONDS must be from OCCASIONAL_WAIT_MIN_SECONDS ({(long)ShortestWait.TotalSeconds}) "
                    + $"to {(long)LongestDelay.TotalSeconds} seconds."
            );

        foreach (var deviceId in ExcludedDevices)
        {
            if (deviceId.Length == 0 || deviceId.IndexOfAny(['/', '+', '#']) >= 0)
                throw new InvalidOperationException(
                    $"OCCASIONAL_EXCLUDED_DEVICES holds '{deviceId}', which is not a device id: each is "
                        + "one topic level, neither empty nor holding '/', '+' or '#'."
                );
        }

        if (!double.IsFinite(Loudness))
            throw new InvalidOperationException("OCCASIONAL_LOUDNESS_LUFS must be a loudness in LUFS.");
    }

    private static string TimeOfDay(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
