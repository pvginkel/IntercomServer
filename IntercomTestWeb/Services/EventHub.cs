using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace IntercomTestWeb.Services;

// Registry of connected /ws/events browser sockets plus a fan-out broadcast. Each connection owns an
// unbounded outbound channel drained by a single writer task, so concurrent broadcasts never issue
// overlapping SendAsync calls on one socket (which WebSocket forbids).
public sealed class EventHub
{
    private static readonly ILogger Logger = Log.ForContext<EventHub>();

    private readonly ConcurrentDictionary<Guid, EventConnection> _connections = new();

    public EventConnection Register(WebSocket socket)
    {
        var connection = new EventConnection(socket);
        _connections[connection.Id] = connection;
        connection.Start();
        return connection;
    }

    public void Unregister(EventConnection connection)
    {
        _connections.TryRemove(connection.Id, out _);
        connection.Complete();
    }

    // Serializes once and enqueues the same bytes to every connection.
    public void Broadcast(object message)
    {
        if (_connections.IsEmpty)
            return;

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(message, IntercomJson.Options);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to serialize an event message");
            return;
        }

        foreach (var connection in _connections.Values)
            connection.Enqueue(bytes);
    }
}

public sealed class EventConnection
{
    private static readonly ILogger Logger = Log.ForContext<EventConnection>();

    private readonly WebSocket _socket;
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    public Guid Id { get; } = Guid.NewGuid();

    public EventConnection(WebSocket socket)
    {
        _socket = socket;
    }

    public void Enqueue(byte[] bytes) => _outbound.Writer.TryWrite(bytes);

    // Serialize-and-send to this one connection (used for the initial state replay).
    public void Send(object message) =>
        Enqueue(JsonSerializer.SerializeToUtf8Bytes(message, IntercomJson.Options));

    public void Start() => _ = WriterLoop();

    public void Complete() => _outbound.Writer.TryComplete();

    private async Task WriterLoop()
    {
        try
        {
            await foreach (var bytes in _outbound.Reader.ReadAllAsync())
            {
                if (_socket.State != WebSocketState.Open)
                    break;

                await _socket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Event connection writer ended");
        }
    }
}
