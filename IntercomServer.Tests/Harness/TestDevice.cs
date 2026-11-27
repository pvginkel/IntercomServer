using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// A device as the server meets one: it publishes its configuration, state and actions to the
/// broker under its prefix, and records what the server sends it, the commands on its
/// <c>set/…</c> topics and the audio on its UDP listener. The JSON it publishes is written out
/// here, field by field, because it is the firmware's wire contract.
/// </summary>
internal sealed class TestDevice : IAsyncDisposable
{
    public const string IntercomPrefix = "intercom/client";

    // How long nothing arrives before NextSoundAsync takes a sound to have ended: fifty chunks' time.
    private static readonly TimeSpan SoundEnded = TimeSpan.FromSeconds(1);

    private readonly IMqttClient _mqtt;
    private readonly UdpClient _udp;
    private readonly Channel<DeviceCommand> _commands = Channel.CreateUnbounded<DeviceCommand>();
    private readonly Channel<AudioPacket> _packets = Channel.CreateUnbounded<AudioPacket>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _receiving;

    private TestDevice(string prefix, string deviceId, IMqttClient mqtt, UdpClient udp)
    {
        Prefix = prefix;
        DeviceId = deviceId;
        _mqtt = mqtt;
        _udp = udp;
        _receiving = ReceiveAudio(_stopping.Token);
    }

    public string Prefix { get; }

    public string DeviceId { get; }

    /// <summary>The device's audio endpoint, a UDP listener on loopback.</summary>
    public IPEndPoint AudioEndpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>
    /// The <c>endpoint</c> the default configuration advertises, the <c>host:port</c> the server
    /// passes to the device's peers in a call.
    /// </summary>
    public string Endpoint => $"{AudioEndpoint.Address}:{AudioEndpoint.Port}";

    /// <summary>How many audio packets the device has received so far.</summary>
    public int ReceivedPacketCount => _packets.Reader.Count;

    /// <summary>How many commands the device has received so far.</summary>
    public int ReceivedCommandCount => _commands.Reader.Count;

    public static async Task<TestDevice> ConnectAsync(TestBroker broker, string prefix, string deviceId)
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var mqtt = await broker.ConnectAsync($"device:{prefix}/{deviceId}");
        var device = new TestDevice(prefix, deviceId, mqtt, udp);

        mqtt.ApplicationMessageReceivedAsync += e =>
        {
            device._commands.Writer.TryWrite(
                new DeviceCommand(
                    e.ApplicationMessage.Topic[(device.Topic("").Length)..],
                    e.ApplicationMessage.ConvertPayloadToString()
                )
            );
            return Task.CompletedTask;
        };

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await mqtt.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter =>
                    filter
                        .WithTopic(device.Topic("set/+"))
                        .WithNoLocal()
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                )
                .Build(),
            timeout.Token
        );

        return device;
    }

    /// <summary>
    /// The configuration a device publishes: its endpoint, and mono 16 kHz 16-bit in and out,
    /// which is what every device in production advertises. A test edits the copy it gets.
    /// </summary>
    public JsonObject DefaultConfiguration() =>
        new()
        {
            ["unique_id"] = DeviceId,
            ["audio_formats"] = new JsonObject { ["in"] = Mono16Khz16Bit(), ["out"] = Mono16Khz16Bit() },
            ["device"] = new JsonObject
            {
                ["manufacturer"] = "IntercomServer.Tests",
                ["model"] = "Test device",
                ["name"] = DeviceId,
                ["firmware_version"] = "0.0.0",
            },
            ["endpoint"] = Endpoint,
        };

    public static JsonObject Mono16Khz16Bit() =>
        new()
        {
            ["channel_layout"] = "mono",
            ["sample_rate"] = 16000,
            ["bit_rate"] = 16,
        };

    /// <summary>Publishes the configuration, retained as the firmware does; null publishes an empty payload.</summary>
    public Task PublishConfigurationAsync(JsonObject? configuration) =>
        PublishAsync("configuration", configuration?.ToJsonString(), retain: true);

    public Task PublishConfigurationAsync() => PublishConfigurationAsync(DefaultConfiguration());

    /// <summary>Publishes the state, retained as the firmware does.</summary>
    public Task PublishStateAsync(bool online = true, bool enabled = true) =>
        PublishAsync(
            "state",
            new JsonObject { ["online"] = online, ["enabled"] = enabled }.ToJsonString(),
            retain: true
        );

    /// <summary>Publishes the default configuration and an online, enabled state.</summary>
    public async Task ComeOnlineAsync()
    {
        await PublishConfigurationAsync();
        await PublishStateAsync();
    }

    public Task ClickAsync() => PublishAsync("set/action", "click");

    public Task LongClickAsync() => PublishAsync("set/action", "long_click");

    /// <summary>
    /// Publishes on one of the device's own topics, at QoS 1: the broker acknowledges only
    /// once it has queued the message to its subscribers, so the server receives it before
    /// anything the test publishes next, from any client.
    /// </summary>
    public async Task PublishAsync(string subtopic, string? payload, bool retain = false)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(Topic(subtopic))
            .WithPayload(payload ?? "")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await _mqtt.PublishAsync(message, timeout.Token);
    }

    public Task<DeviceCommand> NextCommandAsync() =>
        Timeouts.NextAsync(_commands.Reader, $"a command to device {DeviceId}");

    /// <summary>
    /// Asserts the next commands the device receives are <paramref name="expected"/>, in this
    /// order: the server publishes them one after another, and the broker delivers a device's
    /// commands in the order they were published.
    /// </summary>
    public async Task ExpectCommandsAsync(params ExpectedCommand[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            var command = await NextCommandAsync();
            Assert.True(
                expected[i].Matches(command),
                $"Command {i + 1} of {expected.Length} to device {DeviceId}: expected {expected[i]}, got {command.Subtopic} {command.Payload}."
            );
        }
    }

    public Task<AudioPacket> NextPacketAsync() =>
        Timeouts.NextAsync(_packets.Reader, $"an audio packet to device {DeviceId}");

    public async Task<List<AudioPacket>> NextPacketsAsync(int count)
    {
        var packets = new List<AudioPacket>(count);
        for (var i = 0; i < count; i++)
            packets.Add(await NextPacketAsync());
        return packets;
    }

    /// <summary>
    /// The next audio packet, or null when none arrives within <paramref name="quiet"/>: how a
    /// test sees a sound end.
    /// </summary>
    public async Task<AudioPacket?> NextPacketWithinAsync(TimeSpan quiet)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        timeout.CancelAfter(quiet);

        try
        {
            return await _packets.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The packets of the next sound: from the next packet until none arrives for a second, or
    /// until more than <paramref name="longest"/> of audio has come, so a sound that plays on is
    /// returned cut short rather than waited out.
    /// </summary>
    public async Task<List<AudioPacket>> NextSoundAsync(TimeSpan longest)
    {
        var packets = new List<AudioPacket> { await NextPacketAsync() };
        var bytes = packets[0].Payload.Length;

        while (bytes <= BuiltInSounds.Bytes(longest) && await NextPacketWithinAsync(SoundEnded) is { } packet)
        {
            packets.Add(packet);
            bytes += packet.Payload.Length;
        }

        return packets;
    }

    /// <summary>Takes the audio packets that have arrived and not been taken yet, without waiting.</summary>
    public List<AudioPacket> TakeArrivedPackets()
    {
        var packets = new List<AudioPacket>();
        while (_packets.Reader.TryRead(out var packet))
            packets.Add(packet);
        return packets;
    }

    private string Topic(string subtopic) => $"{Prefix}/{DeviceId}/{subtopic}";

    private async Task ReceiveAudio(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await _udp.ReceiveAsync(cancellationToken);
                _packets.Writer.TryWrite(new AudioPacket(result.Buffer, Stopwatch.GetTimestamp()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _receiving;
        _udp.Dispose();
        _stopping.Dispose();

        await _mqtt.DisconnectAsync();
        _mqtt.Dispose();
    }
}

/// <summary>A command the server published on one of the device's topics, such as <c>set/red_led</c>.</summary>
internal sealed record DeviceCommand(string Subtopic, string? Payload)
{
    /// <summary>
    /// Asserts the command is on <paramref name="subtopic"/>, with a JSON payload equal to
    /// <paramref name="json"/> whatever its key order and spacing.
    /// </summary>
    public void AssertIs(string subtopic, string json)
    {
        Assert.Equal(subtopic, Subtopic);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(Payload!)),
            $"Expected {subtopic} {json}, got {Payload}."
        );
    }
}

/// <summary>
/// One datagram the device's UDP listener received: a 4-byte big-endian packet index, then
/// the payload. <see cref="ReceivedAt"/> is a <see cref="Stopwatch"/> timestamp.
/// </summary>
internal sealed record AudioPacket(byte[] Datagram, long ReceivedAt)
{
    public int Index => BinaryPrimitives.ReadInt32BigEndian(Datagram);

    public ReadOnlySpan<byte> Payload => Datagram.AsSpan(4);

    /// <summary>
    /// Whether two runs of packets carry the same audio, payload by payload, as one sound sent to
    /// two devices does.
    /// </summary>
    public static bool SameAudio(IEnumerable<AudioPacket> first, IEnumerable<AudioPacket> second) =>
        first
            .Select(packet => Convert.ToHexString(packet.Payload))
            .SequenceEqual(second.Select(packet => Convert.ToHexString(packet.Payload)));

    /// <summary>How long after <paramref name="earlier"/> this packet arrived.</summary>
    public TimeSpan ArrivedAfter(AudioPacket earlier) =>
        Stopwatch.GetElapsedTime(earlier.ReceivedAt, ReceivedAt);
}
