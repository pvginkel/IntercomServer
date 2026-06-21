using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using IntercomServer.Utils;
using IntercomTestWeb.Audio;
using Serilog;

namespace IntercomTestWeb.Services;

// Bridges one simulated device's audio between the browser (binary WebSocket) and the device UDP
// plane. Owned by the SimDeviceManager handle; created with the device, lives for its lifetime.
//
//   Downlink (device -> browser): device UDP -> AudioMixer(100 ms) -> WS frames to the browser.
//   Uplink   (browser -> device): browser mic frames -> AudioMixer(100 ms) -> UdpAudioServer.Send to
//                                  every endpoint the device advertised (set/add_endpoint), re-framed
//                                  and MTU-fragmented exactly like the WPF mic path.
//
// The WS audio frame is byte-identical to the UDP audio frame (D6): a 4-byte big-endian sequence
// index followed by PCM16LE mono 16 kHz. That lets AudioMixer, the reorder logic, and UdpAudioServer
// run unchanged on both planes — the browser is just "another endpoint" reached over a WebSocket.
//
// The audio path is active only while a browser is attached: with no listener there is nowhere to
// play downlink audio, and no source for uplink. A single 20 ms pump (started on attach, stopped on
// detach) drains both mixers, mirroring the WPF PipeAudio stopwatch loop so coarse OS timer
// resolution is caught up rather than dropped.
public sealed class AudioBridge : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AudioBridge>();

    // 20 ms chunk at 16 kHz/16-bit/mono = 640 bytes.
    private static readonly int ChunkBytes = (int)(
        AudioConstants.AudioFormat.BytesPerSecond * AudioConstants.AudioChunkSize.TotalSeconds
    );

    // Max safe UDP data size assuming a 1500-byte MTU, minus the 4-byte sequence header.
    private const int MaxUdpPayload = 1472 - 4;

    // The uplink mixer keys streams by source endpoint; the browser is a single logical source, so any
    // stable key works.
    private static readonly IPEndPoint BrowserSource = new(IPAddress.None, 0);

    private readonly SimDevice _device;
    private readonly UdpAudioServer _udp;

    private readonly AudioMixer _uplink = new(
        AudioConstants.AudioFormat,
        AudioConstants.BufferInterval
    );
    private readonly AudioMixer _downlink = new(
        AudioConstants.AudioFormat,
        AudioConstants.BufferInterval
    );
    private readonly Lock _uplinkLock = new();
    private readonly Lock _downlinkLock = new();

    private readonly Lock _connectionLock = new();
    // Identifies the connection that currently owns the audio path; a new connection supersedes it.
    private CancellationTokenSource? _currentCts;
    private Task? _pumpTask;

    // Cheap gate for the hot UDP receive path so it doesn't touch the connection lock per datagram.
    private volatile bool _attached;

    private int _uplinkUdpSeq;
    private int _downlinkWsSeq;

    public AudioBridge(SimDevice device, UdpAudioServer udp)
    {
        _device = device;
        _udp = udp;
        _udp.Data += OnUdpData;
    }

    // Runs for the lifetime of one browser audio connection: attaches (starting the pump), pumps mic
    // frames in from the socket, and detaches on close/error.
    public async Task RunBrowserConnection(WebSocket socket, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Attach(socket, cts);
        try
        {
            await ReceiveLoop(socket, cts.Token);
        }
        finally
        {
            await Detach(cts, pump);
        }
    }

    private Task Attach(WebSocket socket, CancellationTokenSource cts)
    {
        lock (_connectionLock)
        {
            // Only one browser per simulated device; a new connection supersedes (and cancels) the
            // previous one.
            _currentCts?.Cancel();
            _currentCts = cts;
            _attached = true;

            lock (_uplinkLock)
                _uplink.Reset();
            lock (_downlinkLock)
                _downlink.Reset();

            var pump = Task.Run(() => PumpLoop(socket, cts.Token));
            _pumpTask = pump;
            return pump;
        }
    }

    private async Task Detach(CancellationTokenSource cts, Task pump)
    {
        // A newer connection may already have taken over; only the still-current owner resets the
        // shared mixers and playing state.
        bool owner;
        lock (_connectionLock)
        {
            owner = _currentCts == cts;
            if (owner)
            {
                _attached = false;
                _currentCts = null;
            }
        }

        cts.Cancel();

        try
        {
            await pump;
        }
        catch
        {
            // Pump faults are already logged.
        }

        cts.Dispose();

        if (owner)
        {
            _device.IsPlaying = false;

            lock (_uplinkLock)
                _uplink.Reset();
            lock (_downlinkLock)
                _downlink.Reset();
        }
    }

    // ---- Downlink: device UDP -> downlink mixer ----

    private void OnUdpData(object? sender, UdpAudioDataEventArgs e)
    {
        // No browser listening -> nowhere to play it; drop rather than fill the buffer.
        if (!_attached)
            return;

        lock (_downlinkLock)
            _downlink.Append(e.RemoteEndpoint, e.Data);

        _device.IsPlaying = true;
    }

    // ---- Uplink: browser WS frame -> uplink mixer ----

    private void OnUplinkFrame(byte[] frame)
    {
        // Needs at least the 4-byte sequence header plus a sample.
        if (frame.Length <= 4)
            return;

        lock (_uplinkLock)
            _uplink.Append(BrowserSource, frame);
    }

    // ---- The 20 ms pump (drains both mixers) ----

    private async Task PumpLoop(WebSocket socket, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var elapsed = TimeSpan.Zero;
        var chunk = AudioConstants.AudioChunkSize;
        var downlinkChunk = new byte[ChunkBytes];

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Catch up if the OS timer fired late (matches the WPF PipeAudio stopwatch loop); the
                // 100 ms mixer absorbs up to that much lateness before underrunning.
                while (stopwatch.Elapsed > elapsed + chunk)
                {
                    await ProcessDownlink(socket, downlinkChunk, token);
                    ProcessUplink();
                    elapsed += chunk;
                }

                await Task.Delay(chunk, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Detached / shutting down.
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Audio pump for {Device} ended", _device.DeviceId);
        }
    }

    private async Task ProcessDownlink(WebSocket socket, byte[] chunk, CancellationToken token)
    {
        bool hasData;
        lock (_downlinkLock)
        {
            hasData = _downlink.HasData;
            if (hasData)
                _downlink.Take(chunk);
        }

        if (!hasData)
        {
            _device.IsPlaying = false;
            return;
        }

        if (socket.State != WebSocketState.Open)
            return;

        var frame = new byte[4 + chunk.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, _downlinkWsSeq++);
        Array.Copy(chunk, 0, frame, 4, chunk.Length);

        await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, token);
    }

    private void ProcessUplink()
    {
        byte[]? chunk = null;
        lock (_uplinkLock)
        {
            if (_uplink.HasData)
            {
                chunk = new byte[ChunkBytes];
                _uplink.Take(chunk);
            }
        }

        if (chunk is null)
            return;

        var endpoints = _device.RemoteEndpoints;
        if (endpoints.IsDefaultOrEmpty)
            return;

        // Re-frame with our own sequence index and MTU-fragment, exactly as the WPF mic path did. A
        // 20 ms chunk (640 bytes) never fragments in practice, but the loop keeps parity.
        for (var offset = 0; offset < chunk.Length; offset += MaxUdpPayload)
        {
            var len = Math.Min(chunk.Length - offset, MaxUdpPayload);
            var packet = new byte[len + 4];
            BinaryPrimitives.WriteInt32BigEndian(packet, _uplinkUdpSeq++);
            Array.Copy(chunk, offset, packet, 4, len);

            foreach (var endpoint in endpoints)
                _udp.Send(endpoint, packet);
        }
    }

    // ---- Browser -> backend receive loop ----

    private async Task ReceiveLoop(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var assembled = new MemoryStream();

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            assembled.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        null,
                        CancellationToken.None
                    );
                    return;
                }

                assembled.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
                OnUplinkFrame(assembled.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        _udp.Data -= OnUdpData;

        Task? pump;
        lock (_connectionLock)
        {
            _attached = false;
            // Cancel the active connection's pump; the connection's own Detach disposes its cts.
            _currentCts?.Cancel();
            pump = _pumpTask;
        }

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch
            {
                // Pump faults are already logged; nothing to do during teardown.
            }
        }
    }
}
