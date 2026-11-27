using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

/// <summary>
/// An ambient track: a file in the store's <c>enabled/</c> or <c>disabled/</c> folder, named by
/// its file name without its extension, lasting <paramref name="Duration"/> as ffprobe reads it.
/// </summary>
internal sealed record Track(string Name, bool Enabled, TimeSpan Duration, string Path);

/// <summary>Why the store refused an upload, a change or a delete.</summary>
internal enum TrackRefusal
{
    /// <summary>The name breaks the rules a track's name keeps.</summary>
    InvalidName,

    /// <summary>No track has the name.</summary>
    NotFound,

    /// <summary>A file in either folder has the name, ignoring case and extension.</summary>
    NameTaken,

    /// <summary>ffprobe finds no audio the server can play in the file.</summary>
    NotAudio,

    /// <summary>The file lasts longer than <see cref="TrackConfiguration.MaxDuration"/>.</summary>
    TooLong,

    /// <summary>The file holds more than <see cref="TrackConfiguration.MaxBytes"/>.</summary>
    TooLarge,
}

/// <summary>
/// What an upload, a change or a delete did: the track as it left it, or, with
/// <see cref="Track"/> null, what the store refused and a sentence saying why.
/// </summary>
internal sealed record TrackResult(Track? Track, TrackRefusal? Refusal, string? Detail)
{
    public static TrackResult Done(Track track) => new(track, null, null);

    public static TrackResult Refused(TrackRefusal refusal, string detail) => new(null, refusal, detail);
}

/// <summary>
/// The ambient tracks, files under <c>&lt;DATA_DIR&gt;/ambient</c>: <c>enabled/</c>,
/// <c>disabled/</c>, and uploads in progress under <c>.incoming/</c>. A name is 1 to
/// <see cref="MaxNameLength"/> characters, with none of <c>/ \ : * ? " &lt; &gt; |</c>, no control
/// characters and no leading or trailing space or dot, and unique across both folders, ignoring
/// case and extension. Rename, enable, disable and delete run under one lock, with the list and
/// the last step of an upload. A file of the folders is a track only when its name keeps the rules,
/// no other file has it, and ffprobe reads audio in it: the folders are the data volume's, which
/// the operator can write by hand.
/// </summary>
internal sealed class TrackStore(DataConfiguration data, TrackConfiguration configuration, AudioProbe probe)
    : IHostedService
{
    /// <summary>The most characters a track's name holds.</summary>
    public const int MaxNameLength = 80;

    private static readonly ILogger Logger = Log.ForContext<TrackStore>();

    private static readonly SearchValues<char> ForbiddenInNames = SearchValues.Create("/\\:*?\"<>|");

    private readonly string _directory = Path.GetFullPath(Path.Combine(data.Directory, "ambient"));

    // Every upload this store stages is named with this prefix, so its start tells an upload in
    // progress, which the web host takes before the start runs, from one an earlier process left.
    private readonly string _session = $"{Guid.NewGuid():N}-";

    private readonly SemaphoreSlim _lock = new(1, 1);

    // Under the lock: what ffprobe found in each file of the folders, by path, with the file's
    // modification time when it was probed; and the files the log has named as no track.
    private Dictionary<string, (DateTime Modified, Probed Probed)> _probed = new();
    private HashSet<string> _leftOut = new();

    private string EnabledDirectory => Path.Combine(_directory, "enabled");

    private string DisabledDirectory => Path.Combine(_directory, "disabled");

    private string IncomingDirectory => Path.Combine(_directory, ".incoming");

    /// <summary>
    /// Stops the start on an ffprobe that is missing: nothing else in a start runs it, so the server
    /// would otherwise read as healthy until the first upload. Deletes the uploads an earlier
    /// process left unfinished.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var version = await probe.VersionAsync(cancellationToken);

        Directory.CreateDirectory(EnabledDirectory);
        Directory.CreateDirectory(DisabledDirectory);
        Directory.CreateDirectory(IncomingDirectory);

        var unfinished = Directory
            .EnumerateFiles(IncomingDirectory)
            .Where(staged => !Path.GetFileName(staged).StartsWith(_session, StringComparison.Ordinal))
            .ToList();
        foreach (var staged in unfinished)
            File.Delete(staged);
        if (unfinished.Count > 0)
            Logger.Information("Deleted {Count} uploads an earlier process left unfinished.", unfinished.Count);

        Logger.Information("Found ffprobe {Version}; the ambient tracks are in {Directory}", version, _directory);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The tracks, sorted by name, ignoring case.</summary>
    public async Task<IReadOnlyList<Track>> ListAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await TracksAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The track named <paramref name="name"/>, ignoring case, or null with none.</summary>
    public async Task<Track?> FindAsync(string name, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return Find(await TracksAsync(cancellationToken), name);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Adds <paramref name="content"/> as an enabled track named <paramref name="name"/>, in the
    /// extension its format is named by. It is written under <c>.incoming</c>, probed, and only
    /// then renamed into <c>enabled/</c>, so a half-written upload is never a track.
    /// </summary>
    public async Task<TrackResult> UploadAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        if (BrokenNameRule(name) is { } broken)
            return TrackResult.Refused(TrackRefusal.InvalidName, broken);

        var staged = Path.Combine(IncomingDirectory, $"{_session}{Guid.NewGuid():N}");
        try
        {
            await using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
            {
                if (!await CopyWithinLimitAsync(content, file, cancellationToken))
                    return TrackResult.Refused(
                        TrackRefusal.TooLarge,
                        $"A track's file holds at most {configuration.MaxBytes} bytes, and this one holds more."
                    );

                file.Flush(flushToDisk: true);
            }

            var probed = await probe.ProbeAsync(staged, cancellationToken);
            if (probed is NotAudio notAudio)
                return TrackResult.Refused(TrackRefusal.NotAudio, NotAudioDetail(notAudio));

            var audio = (ProbedAudio)probed;
            if (audio.Duration > configuration.MaxDuration)
                return TrackResult.Refused(
                    TrackRefusal.TooLong,
                    $"A track lasts at most {Seconds(configuration.MaxDuration)} s, and this one lasts {Seconds(audio.Duration)} s."
                );

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (Holder(name, except: null) is { } holder)
                    return Taken(name, holder);

                var path = Path.Combine(EnabledDirectory, $"{name}.{audio.Extension}");
                File.Move(staged, path);
                _probed[path] = (File.GetLastWriteTimeUtc(path), audio);

                Logger.Information(
                    "Added the ambient track {Track}, {Seconds} s.",
                    Relative(path),
                    Seconds(audio.Duration)
                );

                return TrackResult.Done(new Track(name, true, audio.Duration, path));
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            File.Delete(staged);
        }
    }

    /// <summary>
    /// Renames the track named <paramref name="name"/> to <paramref name="rename"/>, a rename in its
    /// folder, and moves it to <c>enabled/</c> or <c>disabled/</c> as <paramref name="enabled"/>
    /// says; either left null is left as it is.
    /// </summary>
    public async Task<TrackResult> UpdateAsync(
        string name,
        string? rename,
        bool? enabled,
        CancellationToken cancellationToken
    )
    {
        if (rename != null && BrokenNameRule(rename) is { } broken)
            return TrackResult.Refused(TrackRefusal.InvalidName, broken);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (Find(await TracksAsync(cancellationToken), name) is not { } track)
                return NotFound(name);

            if (rename != null && Holder(rename, except: track.Path) is { } holder)
                return Taken(rename, holder);

            var newName = rename ?? track.Name;
            var nowEnabled = enabled ?? track.Enabled;
            var path = Path.Combine(
                nowEnabled ? EnabledDirectory : DisabledDirectory,
                newName + Path.GetExtension(track.Path)
            );

            if (path != track.Path)
            {
                File.Move(track.Path, path);
                _probed[path] = _probed[track.Path];
                _probed.Remove(track.Path);

                Logger.Information(
                    "Moved the ambient track {From} to {To}.",
                    Relative(track.Path),
                    Relative(path)
                );
            }

            return TrackResult.Done(track with { Name = newName, Enabled = nowEnabled, Path = path });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes the track named <paramref name="name"/>.</summary>
    public async Task<TrackResult> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (Find(await TracksAsync(cancellationToken), name) is not { } track)
                return NotFound(name);

            File.Delete(track.Path);
            _probed.Remove(track.Path);

            Logger.Information("Deleted the ambient track {Track}.", Relative(track.Path));

            return TrackResult.Done(track);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Under the lock. A file probed before is probed again only once it has changed.
    private async Task<List<Track>> TracksAsync(CancellationToken cancellationToken)
    {
        var probed = new Dictionary<string, (DateTime Modified, Probed Probed)>();
        var leftOut = new HashSet<string>();
        var tracks = new List<Track>();

        foreach (var named in Files().GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (named.Count() > 1)
            {
                foreach (var file in named)
                    LeaveOut(file.Path, "Another file in the track folders has its name, ignoring case and extension.");
                continue;
            }

            var (path, name, enabled) = named.Single();
            if (BrokenNameRule(name) is { } broken)
            {
                LeaveOut(path, broken);
                continue;
            }

            var modified = File.GetLastWriteTimeUtc(path);
            var found =
                _probed.TryGetValue(path, out var earlier) && earlier.Modified == modified
                    ? earlier.Probed
                    : await probe.ProbeAsync(path, cancellationToken);
            probed[path] = (modified, found);

            switch (found)
            {
                case ProbedAudio audio:
                    tracks.Add(new Track(name, enabled, audio.Duration, path));
                    break;
                case NotAudio notAudio:
                    LeaveOut(path, NotAudioDetail(notAudio));
                    break;
            }
        }

        _probed = probed;
        _leftOut = leftOut;

        return tracks.OrderBy(track => track.Name, StringComparer.OrdinalIgnoreCase).ToList();

        void LeaveOut(string path, string why)
        {
            leftOut.Add(path);
            if (!_leftOut.Contains(path))
                Logger.Warning("{Path} is no ambient track. {Why}", path, why);
        }
    }

    // Every file of the two folders, named as the track it would be.
    private IEnumerable<(string Path, string Name, bool Enabled)> Files() =>
        Directory
            .EnumerateFiles(EnabledDirectory)
            .Select(path => (path, Path.GetFileNameWithoutExtension(path), true))
            .Concat(
                Directory
                    .EnumerateFiles(DisabledDirectory)
                    .Select(path => (path, Path.GetFileNameWithoutExtension(path), false))
            );

    // Under the lock: the file of either folder, track or not, whose name is name, ignoring case
    // and extension, other than the file at except.
    private string? Holder(string name, string? except) =>
        Files()
            .Where(file => file.Path != except && string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .FirstOrDefault();

    private static Track? Find(List<Track> tracks, string name) =>
        tracks.FirstOrDefault(track => string.Equals(track.Name, name, StringComparison.OrdinalIgnoreCase));

    // The sentence saying which of a track name's rules name breaks, or null when it keeps them all.
    private static string? BrokenNameRule(string name)
    {
        if (name.Length == 0)
            return "A track's name cannot be empty.";

        if (name.Length > MaxNameLength)
            return $"A track's name is at most {MaxNameLength} characters, and this one has {name.Length}.";

        if (name.AsSpan().IndexOfAny(ForbiddenInNames) is var forbidden and >= 0)
            return $"A track's name cannot hold '{name[forbidden]}'.";

        if (name.Any(char.IsControl))
            return "A track's name cannot hold control characters.";

        if (char.IsWhiteSpace(name[0]) || name[0] == '.' || char.IsWhiteSpace(name[^1]) || name[^1] == '.')
            return "A track's name cannot start or end with a space or a dot.";

        return null;
    }

    // Copies content to file, reading at most one buffer past the limit: false when content holds more.
    private async Task<bool> CopyWithinLimitAsync(Stream content, Stream file, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copied += read;
            if (copied > configuration.MaxBytes)
                return false;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return true;
    }

    private static string NotAudioDetail(NotAudio notAudio) =>
        $"The file is not audio the server can play: {notAudio.Reason}.";

    private static TrackResult NotFound(string name) =>
        TrackResult.Refused(TrackRefusal.NotFound, $"There is no track named '{name}'.");

    private TrackResult Taken(string name, string holder) =>
        TrackResult.Refused(TrackRefusal.NameTaken, $"The name '{name}' is taken, by {Relative(holder)}.");

    private string Relative(string path) => Path.GetRelativePath(_directory, path);

    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
