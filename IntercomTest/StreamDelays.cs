using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using Serilog;

namespace IntercomTest;

/// <summary>
/// The playout delay of each stream adapts between the mixer's buffer
/// interval and <see cref="MaxDelay"/>. A stream that resumes within
/// <see cref="UnderrunResumeWindow"/> after it drained underran, and grows
/// its delay by one step. A stream of at least
/// <see cref="ShrinkMinStreamDuration"/> that never dipped within one step of
/// draining shrinks it by one step. Delays are learned per remote IP address
/// (not per endpoint, because the jitter is a property of the path to the
/// host, and the source port may change between calls), shared by all
/// mixers, and persisted to a JSON file in the working directory.
/// </summary>
internal static class StreamDelays
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan DelayStep = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan UnderrunResumeWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShrinkMinStreamDuration = TimeSpan.FromSeconds(30);

    private static readonly string PersistencePath = Path.Combine(
        Environment.CurrentDirectory,
        "stream-delays.json"
    );

    private static readonly ILogger Logger = Log.ForContext(typeof(StreamDelays));
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true };
    private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();
    private static readonly object SyncRoot = new();
    private static Dictionary<string, Entry>? _entries;

    public static TimeSpan Uptime => Stopwatch.Elapsed;

    public static TimeSpan StartStream(IPAddress address, TimeSpan defaultDelay)
    {
        lock (SyncRoot)
        {
            var entries = GetEntries(defaultDelay);

            if (!entries.TryGetValue(address.ToString(), out var entry))
            {
                entry = new Entry { Delay = defaultDelay };
                entries.Add(address.ToString(), entry);
            }

            if (entry.Drained.HasValue)
            {
                if (Uptime - entry.Drained.Value < UnderrunResumeWindow)
                {
                    // The stream resumed right after it drained, so the drain
                    // was an underrun rather than the end of a call. Add
                    // headroom.

                    if (entry.Delay < MaxDelay)
                    {
                        var grown = entry.Delay + DelayStep;
                        entry.Delay = grown < MaxDelay ? grown : MaxDelay;

                        Save(entries);
                    }

                    Logger.Warning(
                        "Underrun; delay of remote {Remote} now {Delay} ms",
                        address,
                        entry.Delay.TotalMilliseconds
                    );
                }
                else if (
                    entry.LastDuration >= ShrinkMinStreamDuration
                    && entry.Delay > defaultDelay
                    && entry.LastMinHeadroom + DelayStep > entry.Delay
                )
                {
                    // The previous stream was long enough to be meaningful and
                    // never came close to draining, so give back one step of
                    // delay.

                    var shrunk = entry.Delay - DelayStep;
                    entry.Delay = shrunk > defaultDelay ? shrunk : defaultDelay;

                    Save(entries);

                    Logger.Information(
                        "Decreased delay of remote {Remote} to {Delay} ms",
                        address,
                        entry.Delay.TotalMilliseconds
                    );
                }

                // Use each drain statistic only once.

                entry.Drained = null;
                entry.LastDuration = TimeSpan.Zero;
            }

            return entry.Delay;
        }
    }

    public static void EndStream(IPAddress address, TimeSpan minHeadroom, TimeSpan duration)
    {
        lock (SyncRoot)
        {
            if (_entries == null || !_entries.TryGetValue(address.ToString(), out var entry))
                return;

            entry.Drained = Uptime;
            entry.LastDuration = duration;
            entry.LastMinHeadroom = minHeadroom;
        }
    }

    private static Dictionary<string, Entry> GetEntries(TimeSpan defaultDelay)
    {
        if (_entries == null)
        {
            _entries = new Dictionary<string, Entry>();

            try
            {
                if (File.Exists(PersistencePath))
                {
                    var persisted = JsonSerializer.Deserialize<Dictionary<string, int>>(
                        File.ReadAllText(PersistencePath)
                    )!;

                    foreach (var (address, delayMs) in persisted)
                    {
                        var delay = TimeSpan.FromMilliseconds(delayMs);
                        if (delay < defaultDelay)
                            delay = defaultDelay;
                        if (delay > MaxDelay)
                            delay = MaxDelay;

                        _entries.Add(address, new Entry { Delay = delay });
                    }

                    Logger.Information("Loaded {Count} stream delays", _entries.Count);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load {Path}", PersistencePath);
            }
        }

        return _entries;
    }

    private static void Save(Dictionary<string, Entry> entries)
    {
        try
        {
            var persisted = entries.ToDictionary(
                p => p.Key,
                p => (int)p.Value.Delay.TotalMilliseconds
            );

            File.WriteAllText(
                PersistencePath,
                JsonSerializer.Serialize(persisted, SerializerOptions)
            );
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save {Path}", PersistencePath);
        }
    }

    private class Entry
    {
        public TimeSpan Delay { get; set; }
        public TimeSpan? Drained { get; set; }
        public TimeSpan LastDuration { get; set; }
        public TimeSpan LastMinHeadroom { get; set; }
    }
}
