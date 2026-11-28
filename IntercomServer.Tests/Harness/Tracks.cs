using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using IntercomServer.Utils.Audio;
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

    /// <summary>
    /// A stereo recording FFmpeg makes: <see cref="RecordingAsync"/>'s tone on its left channel,
    /// and silence on its right.
    /// </summary>
    public static Task<byte[]> LeftChannelRecordingAsync(string extension, TimeSpan length)
    {
        var seconds = length.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        return MadeAsync(
            extension,
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:duration={seconds}",
            "-af",
            "pan=stereo|c0=c0"
        );
    }

    /// <summary>A recording FFmpeg makes of silence lasting <paramref name="length"/>.</summary>
    public static Task<byte[]> SilenceAsync(string extension, TimeSpan length) =>
        MadeAsync(
            extension,
            "-f",
            "lavfi",
            "-i",
            "anullsrc=channel_layout=mono:sample_rate=44100",
            "-t",
            length.TotalSeconds.ToString(CultureInfo.InvariantCulture)
        );

    /// <summary>
    /// White noise FFmpeg makes at 48 kHz, lasting <paramref name="length"/>, its peak about −12
    /// dBFS, the same on every run for one <paramref name="seed"/>. No stretch of it repeats
    /// another, so where a loop of it wraps shows in its samples.
    /// </summary>
    public static Task<byte[]> WhiteNoiseAsync(string extension, TimeSpan length, int seed = 1)
    {
        var seconds = length.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        return MadeAsync(
            extension,
            "-f",
            "lavfi",
            "-i",
            $"anoisesrc=duration={seconds}:color=white:amplitude=0.25:seed={seed}"
        );
    }

    /// <summary>
    /// A FLAC file whose header claims a second of audio and that holds none: ffprobe reads a
    /// length in it, and FFmpeg decodes it to no audio.
    /// </summary>
    public static async Task<byte[]> EmptyFlacAsync()
    {
        var flac = (
            await MadeAsync("flac", "-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=44100", "-t", "0")
        ).ToArray();

        // STREAMINFO, the first metadata block, follows the 4-byte marker and its own 4-byte
        // header. From its 11th byte, 64 bits hold the sample rate (20), the channels (3), the bits
        // per sample (5) and the total of samples (36), which a second's worth replaces.
        var packed = flac.AsSpan(8 + 10, 8);
        var fields = BinaryPrimitives.ReadUInt64BigEndian(packed);
        var sampleRate = fields >> 44;
        BinaryPrimitives.WriteUInt64BigEndian(packed, (fields & ~((1UL << 36) - 1)) | sampleRate);
        return flac;
    }

    /// <summary>
    /// A WAV file whose header declares a second of MP3 audio, and whose data is
    /// <see cref="Noise"/>: ffprobe reads an audio stream and a length in it, and FFmpeg cannot
    /// decode it.
    /// </summary>
    public static byte[] UndecodableWav()
    {
        var data = Noise();
        var wav = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)0x55); // MPEG layer 3
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(data.Length); // bytes a second
        writer.Write((short)1);
        writer.Write((short)0);
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        return wav.ToArray();
    }

    /// <summary>A WAV file of <paramref name="samples"/>, 16-bit PCM in <paramref name="format"/>.</summary>
    public static byte[] Wav(short[] samples, AudioFormat format)
    {
        var wav = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)format.ChannelCount);
        writer.Write(format.SampleRate);
        writer.Write(format.BytesPerSecond);
        writer.Write((short)(format.ChannelCount * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
            writer.Write(sample);
        writer.Flush();
        return wav.ToArray();
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
