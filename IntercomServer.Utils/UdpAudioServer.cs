using System.Net;
using System.Net.Sockets;

namespace IntercomServer.Utils;

/// <summary>
/// A small UDP server for audio packets. It receives datagrams and raises
/// <see cref="Data"/> for each one (with the source endpoint, so consumers can tell
/// streams apart), and it can send datagrams on the same socket. Packet payloads are
/// passed through verbatim; any framing (such as a sequence header) is the caller's
/// concern.
/// </summary>
public sealed class UdpAudioServer : IDisposable
{
    private readonly UdpClient _client;
    private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> _sendQueue = new();
    private readonly Lock _syncRoot = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _sendLoopRunning;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

    public event EventHandler<UdpAudioDataEventArgs>? Data;

    public UdpAudioServer(int port)
    {
        _client = new UdpClient(port);

        _ = ReceiveLoop();
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(_cts.Token);

                Data?.Invoke(this, new UdpAudioDataEventArgs(result.RemoteEndPoint, result.Buffer));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public void Send(IPEndPoint endPoint, byte[] data)
    {
        lock (_syncRoot)
        {
            _sendQueue.Enqueue((endPoint, data));

            if (!_sendLoopRunning)
            {
                _sendLoopRunning = true;

                _ = SendLoop();
            }
        }
    }

    private async Task SendLoop()
    {
        while (true)
        {
            (IPEndPoint EndPoint, byte[] Data) item;

            lock (_syncRoot)
            {
                if (!_sendQueue.TryDequeue(out item!))
                {
                    _sendLoopRunning = false;
                    return;
                }
            }

            await _client.SendAsync(item.Data, item.EndPoint);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _cts.Dispose();
    }
}

public sealed class UdpAudioDataEventArgs(IPEndPoint remoteEndpoint, byte[] data) : EventArgs
{
    public IPEndPoint RemoteEndpoint { get; } = remoteEndpoint;

    public byte[] Data { get; } = data;
}
