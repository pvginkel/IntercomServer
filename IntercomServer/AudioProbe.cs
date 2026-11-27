using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IntercomServer;

/// <summary>What ffprobe found in a file: audio, or why it is not.</summary>
internal abstract record Probed;

/// <summary>
/// A file with an audio stream: its length, and the extension its format is named by
/// (<c>m4a</c>, <c>mp3</c>, <c>wav</c>…).
/// </summary>
internal sealed record ProbedAudio(string Extension, TimeSpan Duration) : Probed;

/// <summary>A file the server cannot play: <paramref name="Reason"/> says why, as a clause.</summary>
internal sealed record NotAudio(string Reason) : Probed;

/// <summary>Reads what a file holds with ffprobe, run as a process on <paramref name="executable"/>.</summary>
internal partial class AudioProbe(string executable)
{
    // ffprobe names a format by the demuxers that read it. A name it gives a family of formats
    // has an extension of its own; any other is the first demuxer's name.
    private static readonly Dictionary<string, string> Extensions = new()
    {
        ["mov,mp4,m4a,3gp,3g2,mj2"] = "m4a",
        ["matroska,webm"] = "webm",
    };

    /// <summary>The version of ffprobe, as its <c>-version</c> names it.</summary>
    public async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        var (exitCode, output, error) = await RunAsync(["-version"], cancellationToken);
        var firstLine = output.Split('\n')[0].Trim();

        if (exitCode != 0 || VersionLine().Match(firstLine) is not { Success: true } version)
            throw new InvalidOperationException(
                $"'{executable} -version' exited with code {exitCode}, printing '{firstLine}' and not ffprobe's version. {error}".TrimEnd()
            );

        return version.Groups[1].Value;
    }

    /// <summary>Probes the file at <paramref name="path"/>, an absolute path.</summary>
    public async Task<Probed> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        // The file: protocol, so a path never reads as another of FFmpeg's protocols.
        var input = $"file:{path}";
        var (exitCode, output, error) = await RunAsync(
            ["-v", "error", "-show_entries", "stream=codec_type:format=format_name,duration", "-of", "json", input],
            cancellationToken
        );

        if (exitCode != 0)
            return new NotAudio($"ffprobe cannot read it: {error.Replace($"{input}: ", "")}");

        var probed = JsonNode.Parse(output)!;

        if (probed["streams"]?.AsArray().Any(stream => (string?)stream?["codec_type"] == "audio") != true)
            return new NotAudio("it holds no audio stream");

        if (
            !double.TryParse(
                (string?)probed["format"]?["duration"],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds
            )
            || !double.IsFinite(seconds)
            || seconds <= 0
        )
            return new NotAudio("ffprobe finds no length in it");

        var formatName = (string)probed["format"]!["format_name"]!;
        var extension = Extensions.GetValueOrDefault(formatName) ?? formatName.Split(',')[0];
        // The extension becomes part of a file name.
        if (!DemuxerName().IsMatch(extension))
            throw new InvalidOperationException(
                $"ffprobe named the format of {path} '{formatName}', which is no demuxer's name."
            );

        return new ProbedAudio(extension, TimeSpan.FromSeconds(seconds));
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
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
                $"ffprobe is missing: the server probes the ambient tracks with it, and '{executable}' could not be started: {ex.Message}",
                ex
            );
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await output, (await error).Trim());
    }

    [GeneratedRegex(@"^ffprobe version (\S+)")]
    private static partial Regex VersionLine();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex DemuxerName();
}
