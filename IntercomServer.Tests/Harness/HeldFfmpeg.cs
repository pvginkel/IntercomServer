using System.Diagnostics;
using System.Runtime.Versioning;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The FFmpeg on the PATH, run by a script under a name of its own: while <see cref="Hold"/> is
/// in force, each run waits before it starts FFmpeg, so a test catches a decode while it runs.
/// Linux names a script's process by the script's name, so a test finds the runs its server
/// started among those other tests run meanwhile.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class HeldFfmpeg : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"intercom-server-tests-{Guid.NewGuid():N}"
    );

    // Linux keeps the first 15 characters of a process's name.
    private readonly string _name = $"ffmpeg-{Guid.NewGuid():N}"[..15];

    public HeldFfmpeg()
    {
        var onPath = Environment
            .GetEnvironmentVariable("PATH")!
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "ffmpeg"))
            .First(File.Exists);
        Directory.CreateDirectory(_directory);
        Executable = Path.Combine(_directory, _name);
        File.WriteAllText(
            Executable,
            $"""
            #!/bin/sh
            while [ -e '{HoldFile}' ]; do touch '{HeldFile}'; sleep 0.01; done
            exec '{onPath}' "$@"

            """
        );
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The script, which an <see cref="IntercomServer.AudioDecoder"/> runs as its FFmpeg.</summary>
    public string Executable { get; }

    private string HoldFile => Path.Combine(_directory, "hold");

    private string HeldFile => Path.Combine(_directory, "held");

    /// <summary>Holds each run that starts from now on until <see cref="Release"/>.</summary>
    public void Hold() => File.WriteAllText(HoldFile, "");

    public void Release() => File.Delete(HoldFile);

    /// <summary>Waits until a run is held.</summary>
    public async Task HeldAsync()
    {
        var waited = Stopwatch.StartNew();
        while (!File.Exists(HeldFile))
        {
            if (waited.Elapsed > Timeouts.Observe)
                throw new TimeoutException($"Waited {Timeouts.Observe.TotalSeconds:0} s for a run of FFmpeg to be held.");

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The ids of the processes that run this script and have not yet started FFmpeg.</summary>
    public int[] Processes()
    {
        var processes = Process.GetProcessesByName(_name);
        var ids = processes.Select(process => process.Id).ToArray();
        foreach (var process in processes)
            process.Dispose();
        return ids;
    }

    public void Dispose()
    {
        Release();
        Directory.Delete(_directory, recursive: true);
    }
}
