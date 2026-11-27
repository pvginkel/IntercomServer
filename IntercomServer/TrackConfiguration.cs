namespace IntercomServer;

/// <summary>The limits an ambient track's upload is held to.</summary>
internal class TrackConfiguration
{
    /// <summary>The longest a track may last, as ffprobe reads it (<c>TRACK_MAX_SECONDS</c>): 15 minutes.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The most bytes a track's file may hold (<c>TRACK_MAX_BYTES</c>): 50 MiB.</summary>
    public long MaxBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>Stops the start on a limit that lets no track through.</summary>
    public void Validate()
    {
        if (MaxDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("TRACK_MAX_SECONDS must be a positive number of seconds.");

        if (MaxBytes <= 0)
            throw new InvalidOperationException("TRACK_MAX_BYTES must be a positive number of bytes.");
    }
}
