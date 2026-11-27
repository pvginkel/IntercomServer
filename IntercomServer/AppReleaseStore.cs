using System.Globalization;
using Serilog;

namespace IntercomServer;

/// <summary>A published release of the app: its version code, and its APK on the data volume.</summary>
internal sealed record AppRelease(int VersionCode, string Path);

/// <summary>
/// The app's releases, <c>&lt;DATA_DIR&gt;/updates/&lt;version_code&gt;.apk</c>. A release is written
/// to a temporary file in that folder and renamed into place, so a half-written APK is never
/// offered, and the newest three are kept.
/// </summary>
internal sealed class AppReleaseStore(DataConfiguration configuration)
{
    private const int Kept = 3;
    private const string TemporaryPattern = ".*.tmp";

    private static readonly ILogger Logger = Log.ForContext<AppReleaseStore>();

    private readonly string _directory = Path.GetFullPath(Path.Combine(configuration.Directory, "updates"));
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The release with the highest version code, or null with none published.</summary>
    public AppRelease? Latest() => Releases().MaxBy(release => release.VersionCode);

    /// <summary>
    /// Publishes <paramref name="apk"/> as <paramref name="versionCode"/>, and deletes the releases
    /// older than the newest three. Returns false, writing nothing, for a version code already
    /// published.
    /// </summary>
    public async Task<bool> PublishAsync(int versionCode, Stream apk, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var path = PathOf(versionCode);
            if (File.Exists(path))
                return false;

            Directory.CreateDirectory(_directory);
            var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    await apk.CopyToAsync(file, cancellationToken);
                    file.Flush(flushToDisk: true);
                }

                File.Move(temporary, path);
            }
            finally
            {
                File.Delete(temporary);
            }

            Logger.Information("Published app release {VersionCode}.", versionCode);

            Prune();

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Runs under the lock, so a temporary file is one a publish the process did not finish left.
    private void Prune()
    {
        foreach (var release in Releases().OrderByDescending(release => release.VersionCode).Skip(Kept))
        {
            File.Delete(release.Path);
            Logger.Information(
                "Deleted app release {VersionCode}, older than the newest {Kept}.",
                release.VersionCode,
                Kept
            );
        }

        foreach (var temporary in Directory.EnumerateFiles(_directory, TemporaryPattern))
            File.Delete(temporary);
    }

    private string PathOf(int versionCode) =>
        Path.Combine(_directory, $"{versionCode.ToString(CultureInfo.InvariantCulture)}.apk");

    // A file of the folder is a release when it is named as one: a positive version code, as
    // PathOf writes it, and .apk. Anything else in it is not offered.
    private IEnumerable<AppRelease> Releases()
    {
        if (!Directory.Exists(_directory))
            return [];

        return Directory
            .EnumerateFiles(_directory, "*.apk")
            .Select(path =>
                int.TryParse(
                    Path.GetFileNameWithoutExtension(path),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var versionCode
                )
                && versionCode > 0
                && PathOf(versionCode) == path
                    ? new AppRelease(versionCode, path)
                    : null
            )
            .OfType<AppRelease>();
    }
}
