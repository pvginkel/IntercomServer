using System.IO;
using NAudio.Wave;
using Serilog;

namespace IntercomTest;

internal class AudioRecorderServer : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AudioRecorderServer>();

    private readonly IntercomUDPServer _udpServer = new(5139);
    private readonly Lock _syncRoot = new();
    private WaveFileWriter? _writer;
    private readonly Timer _stopTimer;
    private bool _disposed;

    public AudioRecorderServer()
    {
        _udpServer.Data += _udpServer_Data;
        _stopTimer = new Timer(StopRecordingCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void StopRecordingCallback(object? state)
    {
        lock (_syncRoot)
        {
            _writer?.Dispose();
            _writer = null;
        }

        Logger.Information("Stopped dumping audio data");
    }

    private void _udpServer_Data(object? sender, IntercomUDPDataEventArgs e)
    {
        lock (_syncRoot)
        {
            _stopTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

            if (_writer == null)
            {
                var fileName = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".wav";

                Log.Information(
                    "Start dumping audio data to {FileName}",
                    Path.GetFullPath(fileName)
                );

                _writer = new WaveFileWriter(fileName, new WaveFormat(16000, 16, 2));
            }

            _writer.Write(e.Data);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _udpServer.Dispose();
            _writer?.Dispose();
            _stopTimer.Dispose();

            _disposed = true;
        }
    }
}
