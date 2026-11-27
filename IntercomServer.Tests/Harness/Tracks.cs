using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The ambient tracks, driven through the server's <see cref="IntercomServer.TrackStore"/>, and
/// the files the real FFmpeg makes to upload.
/// </summary>
internal static class Tracks
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Made = new();

    public static TrackStore TrackStore(this ServerHarness harness) =>
        harness.Services.GetRequiredService<TrackStore>();

    /// <summary>The server's folder of tracks, <c>&lt;DATA_DIR&gt;/ambient</c>.</summary>
    public static string AmbientDirectory(this ServerHarness harness) =>
        Path.Combine(harness.DataDirectory, "ambient");

    /// <summary>The names of the files in <paramref name="folder"/> of <see cref="AmbientDirectory"/>, sorted.</summary>
    public static string[] AmbientFiles(this ServerHarness harness, string folder) =>
        Directory
            .GetFiles(Path.Combine(harness.AmbientDirectory(), folder))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Uploads <paramref name="content"/> as the track <paramref name="name"/>.</summary>
    public static Task<TrackResult> UploadAsync(this ServerHarness harness, string name, byte[] content) =>
        harness.UploadAsync(name, new MemoryStream(content));

    public static Task<TrackResult> UploadAsync(this ServerHarness harness, string name, Stream content) =>
        harness.TrackStore().UploadAsync(name, content, TestContext.Current.CancellationToken);

    /// <summary>
    /// A recording FFmpeg makes: a 440 Hz tone lasting <paramref name="length"/>, in the format
    /// <paramref name="extension"/> names, as FFmpeg encodes it by default.
    /// </summary>
    public static Task<byte[]> RecordingAsync(string extension, TimeSpan length)
    {
        var seconds = length.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        return MadeAsync(extension, "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}");
    }

    /// <summary>An MP4 file FFmpeg makes with a video stream and no audio.</summary>
    public static Task<byte[]> VideoAsync() =>
        MadeAsync("mp4", "-f", "lavfi", "-i", "color=c=black:s=16x16:d=1", "-c:v", "mpeg4");

    /// <summary>4,096 bytes of noise, the same on every run, which ffprobe cannot read.</summary>
    public static byte[] Noise()
    {
        // A fixed seed: fresh random bytes are now and then a file ffprobe reads, as an lrc subtitle.
        var noise = new byte[4096];
        new Random(0).NextBytes(noise);
        return noise;
    }

    // One FFmpeg run serves every test that asks for the same file.
    private static Task<byte[]> MadeAsync(string extension, params string[] arguments) =>
        Made.GetOrAdd(
                $"{extension} {string.Join(' ', arguments)}",
                _ => new Lazy<Task<byte[]>>(() => RunFfmpegAsync(extension, arguments))
            )
            .Value;

    private static async Task<byte[]> RunFfmpegAsync(string extension, string[] arguments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"intercom-server-tests-{Guid.NewGuid():N}.{extension}");
        try
        {
            var startInfo = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in (string[])["-nostdin", "-v", "error", .. arguments, path])
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg could not make a {extension} file: {error}");

            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Serves <paramref name="bytes"/> to a reader, and holds it at <paramref name="holdAt"/> until
/// <see cref="Release"/>: an upload still arriving.
/// </summary>
internal sealed class HeldStream(byte[] bytes, int holdAt) : Stream
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    /// <summary>Completes once the reader has read up to <paramref name="holdAt"/> and asked for more.</summary>
    public Task Held => _held.Task;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position == holdAt)
        {
            _held.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }

        var end = _position < holdAt ? holdAt : bytes.Length;
        var count = Math.Min(buffer.Length, end - _position);
        bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
