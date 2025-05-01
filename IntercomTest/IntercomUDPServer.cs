using System.Net;
using System.Net.Sockets;

namespace IntercomTest;

internal class IntercomUDPServer : IDisposable
{
    private readonly ManualResetEventSlim _event = new();
    private readonly UdpClient _client;
    private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> _sendQueue = new();
    private readonly Lock _syncRoot = new();
    private bool _sendLoopRunning;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

    public event EventHandler<IntercomUDPDataEventArgs>? Data;

    public IntercomUDPServer(int port)
    {
        _client = new UdpClient(port);

        TaskUtils.Run(ReceiveLoop);
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (true)
            {
                var result = await _client.ReceiveAsync();

                OnData(new IntercomUDPDataEventArgs(result.RemoteEndPoint, result.Buffer));
            }
        }
        finally
        {
            _event.Set();
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

                TaskUtils.Run(SendLoop);
            }
        }
    }

    private async Task SendLoop()
    {
        while (true)
        {
            (IPEndPoint EndPoint, byte[] Data) data;

            lock (_syncRoot)
            {
                if (!_sendQueue.TryDequeue(out data!))
                {
                    _sendLoopRunning = false;
                    return;
                }
            }

            await _client.SendAsync(data.Data, data.EndPoint);
        }
    }

    protected virtual void OnData(IntercomUDPDataEventArgs e) => Data?.Invoke(this, e);

    public void Dispose()
    {
        _client.Dispose();
    }
}
