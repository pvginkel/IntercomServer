using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using IntercomServer.Utils.Audio;

namespace IntercomServer;

/// <summary>
/// Decodes audio with FFmpeg, run as a process on <paramref name="executable"/>: the encoded
/// audio goes in on its standard input, and PCM comes out of its standard output.
/// </summary>
internal class AudioDecoder(string executable)
{
    /// <summary>
    /// Decodes <paramref name="input"/> into 16-bit little-endian PCM at the sample rate and
    /// channel count of <paramref name="format"/>. <paramref name="name"/> names the audio in an
    /// error.
    /// </summary>
    public async Task<byte[]> DecodeAsync(
        string name,
        Stream input,
        AudioFormat format,
        CancellationToken cancellationToken
    )
    {
        var sampleFormat = format.BitRate switch
        {
            16 => "s16le",
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format.BitRate,
                "FFmpeg decodes into 16-bit PCM only."
            ),
        };

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] arguments =
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            "pipe:0",
            "-f",
            sampleFormat,
            "-ac",
            format.ChannelCount.ToString(CultureInfo.InvariantCulture),
            "-ar",
            format.SampleRate.ToString(CultureInfo.InvariantCulture),
            "pipe:1",
        ];
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"FFmpeg is missing: the server decodes its sounds with it, and '{executable}' could not be started: {ex.Message}",
                ex
            );
        }

        // Input, output and errors move at once: FFmpeg blocks on a full output pipe while its
        // input is still being written.
        var writing = WriteInputAsync(process, input, cancellationToken);
        var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await reading;
        var error = (await errors).Trim();

        try
        {
            await writing;
        }
        catch (IOException ex) when (process.ExitCode != 0)
        {
            // FFmpeg closed its input when it failed, and its exit says why.
            throw Failed(name, process.ExitCode, error, ex);
        }

        if (process.ExitCode != 0)
            throw Failed(name, process.ExitCode, error, null);

        return output.ToArray();
    }

    private static async Task WriteInputAsync(
        Process process,
        Stream input,
        CancellationToken cancellationToken
    )
    {
        await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
        process.StandardInput.Close();
    }

    private static InvalidOperationException Failed(
        string name,
        int exitCode,
        string error,
        Exception? inner
    ) =>
        new($"FFmpeg could not decode {name}: it exited with code {exitCode}. {error}".TrimEnd(), inner);
}
