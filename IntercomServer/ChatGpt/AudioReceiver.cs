using System.Net;
using System.Net.Sockets;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// Listens for inbound UDP audio from devices. During a ChatGPT conversation a
/// device is told (via <c>add_endpoint</c>) to stream its microphone here. Each
/// packet carries a 4-byte big-endian sequence header (see
/// <see cref="AudioSender"/>) which is stripped before the raw PCM is raised.
/// </summary>
internal sealed class AudioReceiver : IDisposable
{
    private const int HeaderSize = 4;

    private static readonly ILogger Logger = Log.ForContext<AudioReceiver>();

    private readonly UdpClient _client;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public event EventHandler<AudioReceivedEventArgs>? AudioReceived;

    public AudioReceiver(int port)
    {
        _client = new UdpClient(port);
        Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

        _ = ReceiveLoop();
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(_cts.Token);

                if (result.Buffer.Length <= HeaderSize)
                    continue;

                var pcm = result.Buffer.AsMemory(HeaderSize);

                AudioReceived?.Invoke(
                    this,
                    new AudioReceivedEventArgs(result.RemoteEndPoint, pcm)
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Audio receive loop failed");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _cts.Dispose();
    }
}

internal sealed class AudioReceivedEventArgs(IPEndPoint source, ReadOnlyMemory<byte> pcm)
    : EventArgs
{
    public IPEndPoint Source { get; } = source;
    public ReadOnlyMemory<byte> Pcm { get; } = pcm;
}
