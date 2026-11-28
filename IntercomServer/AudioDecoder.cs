using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using IntercomServer.Utils.Audio;

namespace IntercomServer;

/// <summary>
/// Decodes audio with FFmpeg, run as a process on <paramref name="executable"/>: the encoded
/// audio goes in on its standard input, and PCM comes out of its standard output. A decode that
/// fails or is abandoned leaves no FFmpeg running.
/// </summary>
internal partial class AudioDecoder(string executable)
{
    /// <summary>
    /// Decodes <paramref name="input"/> into 16-bit little-endian PCM at the sample rate and
    /// channel count of <paramref name="format"/>. <paramref name="name"/> names the audio in an
    /// error. A read of <paramref name="input"/> that fails ends the decode with that read's error.
    /// </summary>
    public async Task<byte[]> DecodeAsync(
        string name,
        Stream input,
        AudioFormat format,
        CancellationToken cancellationToken
    )
    {
        Require16Bit(format);

        var (pcm, _) = await RunAsync(
            name,
            input,
            [
                "-f",
                "s16le",
                "-ac",
                format.ChannelCount.ToString(CultureInfo.InvariantCulture),
                "-ar",
                format.SampleRate.ToString(CultureInfo.InvariantCulture),
            ],
            cancellationToken
        );
        return pcm;
    }

    /// <summary>
    /// Decodes <paramref name="input"/> as <see cref="DecodeAsync"/> does, and measures it in the
    /// same run with FFmpeg's EBU R128 meter. The meter reads the PCM returned: the audio mixed
    /// down to the channels of <paramref name="format"/> and resampled to its rate, never the
    /// input's own channels.
    /// </summary>
    public async Task<MeasuredAudio> DecodeMeasuredAsync(
        string name,
        Stream input,
        AudioFormat format,
        CancellationToken cancellationToken
    )
    {
        Require16Bit(format);
        var channelLayout = format.ChannelLayout switch
        {
            AudioChannelLayout.Mono => "mono",
            AudioChannelLayout.Stereo => "stereo",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format.ChannelLayout, null),
        };
        var sampleRate = format.SampleRate.ToString(CultureInfo.InvariantCulture);

        var (pcm, log) = await RunAsync(
            name,
            input,
            [
                // FFmpeg's -ac and -ar convert after the whole filter chain, so the conversion
                // into the format sent is filtered ahead of the meter instead. Its frame log is
                // verbose, below the level FFmpeg logs at; its summary is not.
                "-af",
                $"aformat=sample_fmts=s16:channel_layouts={channelLayout}:sample_rates={sampleRate},"
                    + "ebur128=peak=sample:framelog=verbose",
                "-f",
                "s16le",
            ],
            cancellationToken
        );

        var summary =
            log.SingleOrDefault(message =>
                message.Context.StartsWith("[Parsed_ebur128_", StringComparison.Ordinal)
                && message.Text.StartsWith("Summary:", StringComparison.Ordinal)
            )
            ?? throw new InvalidOperationException(
                $"FFmpeg decoded {name}, but its EBU R128 meter logged no summary."
            );
        var loudness = IntegratedLoudness().Match(summary.Text);
        var peak = SamplePeak().Match(summary.Text);
        if (!loudness.Success || !peak.Success)
            throw new InvalidOperationException(
                $"FFmpeg decoded {name}, but its EBU R128 meter's summary has no integrated loudness or sample peak: {summary.Text}"
            );

        return new MeasuredAudio(pcm, Level(loudness), Level(peak));
    }

    private async Task<(byte[] Pcm, List<LogMessage> Log)> RunAsync(
        string name,
        Stream input,
        string[] outputArguments,
        CancellationToken cancellationToken
    )
    {
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
            "-nostats",
            // FFmpeg reads keys from its standard input unless its input is named pipe:, and
            // cache:pipe:0 is not, so it would take bytes of the audio for keys.
            "-nostdin",
            // Each message comes tagged with its level, which tells the errors from the meter's
            // summary, logged at info.
            "-loglevel",
            "level+info",
            // A pipe cannot seek, and an MP4 that keeps its index after its audio, as FFmpeg and
            // phones write them, is read by seeking back: the cache protocol keeps what FFmpeg
            // has read in a temporary file of its own, and seeks in that.
            "-read_ahead_limit",
            "-1",
            "-i",
            "cache:pipe:0",
            .. outputArguments,
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

        try
        {
            // Input, output and errors move at once: FFmpeg blocks on a full output pipe while
            // its input is still being written.
            var writing = WriteInputAsync(input, process.StandardInput.BaseStream, cancellationToken);
            var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);

            // A read of the input that fails ends the decode here, with that read's error.
            var refused = await writing;
            await process.WaitForExitAsync(cancellationToken);
            await reading;
            var log = LogMessages(await errors);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"FFmpeg could not decode {name}: it exited with code {process.ExitCode}. {Errors(log)}".TrimEnd(),
                    refused
                );

            return (output.ToArray(), log);
        }
        finally
        {
            // A no-op on an FFmpeg that has exited.
            process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Copies <paramref name="input"/> to FFmpeg's input, which it closes however the copy ends.
    /// A write FFmpeg refuses, having closed its input, ends the copy with that write's error
    /// returned: FFmpeg's exit says whether the decode failed.
    /// </summary>
    private static async Task<IOException?> WriteInputAsync(
        Stream input,
        Stream ffmpeg,
        CancellationToken cancellationToken
    )
    {
        // The pipe itself, not the StandardInput writer around it, whose close flushes, which
        // fails on a pipe FFmpeg has closed. A Process leaves an input it handed out unclosed.
        await using (ffmpeg)
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                try
                {
                    await ffmpeg.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                catch (IOException ex)
                {
                    return ex;
                }
            }

            return null;
        }
    }

    private static void Require16Bit(AudioFormat format)
    {
        if (format.BitRate != 16)
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format.BitRate,
                "FFmpeg decodes into 16-bit PCM only."
            );
    }

    /// <summary>
    /// FFmpeg's log as its messages. FFmpeg tags the first line of each, so a line without a tag
    /// goes on with the message before it.
    /// </summary>
    private static List<LogMessage> LogMessages(string log)
    {
        var messages = new List<LogMessage>();
        foreach (var line in log.Split('\n'))
        {
            var start = MessageStart().Match(line);
            if (start.Success)
                messages.Add(
                    new LogMessage(
                        start.Groups["level"].Value,
                        start.Groups["context"].Value,
                        start.Groups["text"].Value
                    )
                );
            else if (messages.Count > 0)
                messages[^1] = messages[^1] with { Text = messages[^1].Text + "\n" + line };
        }

        return messages;
    }

    /// <summary>The errors of <paramref name="log"/>, as FFmpeg logs them without their level.</summary>
    private static string Errors(List<LogMessage> log) =>
        string.Join(
            '\n',
            log.Where(message =>
                    message.Level is "error" or "fatal" or "panic"
                    // The cache protocol logs one as an error on every decode: it asks the pipe
                    // for its size, which a pipe cannot answer.
                    && !message.Text.StartsWith("Inner protocol failed to seekback end", StringComparison.Ordinal)
                )
                .Select(message => (message.Context + message.Text).TrimEnd())
        );

    // FFmpeg prints a level as %5.1f, and silence's peak as -inf.
    private static double Level(Match match) =>
        match.Groups["value"].Value == "-inf"
            ? double.NegativeInfinity
            : double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// A message's first line at <c>-loglevel level+info</c>: the tags of the contexts it comes
    /// from, then its level's.
    /// </summary>
    [GeneratedRegex(@"^(?<context>(?:\[[^\]]+ @ 0x[0-9a-f]+\] )*)\[(?<level>[a-z]+)\] (?<text>.*)$")]
    private static partial Regex MessageStart();

    [GeneratedRegex(@"^\s*I:\s+(?<value>-?\d+\.\d|-inf) LUFS$", RegexOptions.Multiline)]
    private static partial Regex IntegratedLoudness();

    [GeneratedRegex(@"^\s*Peak:\s+(?<value>-?\d+\.\d|-inf) dBFS$", RegexOptions.Multiline)]
    private static partial Regex SamplePeak();

    private sealed record LogMessage(string Level, string Context, string Text);
}

/// <summary>
/// PCM that FFmpeg decoded, with its EBU R128 measurement: <paramref name="Loudness"/> is its
/// integrated loudness in LUFS, −70, the meter's floor, for silence; <paramref name="Peak"/> is its
/// sample peak in dBFS, negative infinity for silence.
/// </summary>
internal sealed record MeasuredAudio(byte[] Pcm, double Loudness, double Peak);
