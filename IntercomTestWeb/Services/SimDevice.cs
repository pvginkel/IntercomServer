using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using IntercomServer.Utils;
using MQTTnet;
using Serilog;

namespace IntercomTestWeb.Services;

// Ported from IntercomTest's Device — the in-memory model of one simulated intercom. Phase A keeps
// the full MQTT-facing behavior (responds to set/recording, set/red_led, set/green_led,
// set/add_endpoint, set/remove_endpoint; advertises a configuration + state) and drops the WPF/WASAPI
// audio relay (the AudioMixer and Append/Take/Reset), which Phase B reintroduces. The endpoint set is
// already tracked here so Phase B can send audio to it.
//
// Unlike the WPF original this raises StateChanged synchronously (no WPF SynchronizationContext);
// callers (SimDeviceClient / SimDeviceManager) are responsible for marshalling. Mutations only ever
// happen from HandleMessage, which the client serializes under a lock.
public sealed class SimDevice(string deviceId, IPEndPoint localEndPoint)
{
    private static readonly ILogger Logger = Log.ForContext<SimDevice>();

    private bool _isPlaying;
    private bool _isRecording;
    private DeviceLedAction? _redLed;
    private DeviceLedAction? _greenLed;

    public string DeviceId { get; } = deviceId;

    public bool IsPlaying
    {
        get => _isPlaying;
        // Driven by the audio downlink in Phase B; stays false in Phase A.
        internal set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                OnStateChanged();
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (_isRecording != value)
            {
                _isRecording = value;
                OnStateChanged();
            }
        }
    }

    public DeviceLedAction? RedLed
    {
        get => _redLed;
        private set
        {
            _redLed = value;
            OnStateChanged();
        }
    }

    public DeviceLedAction? GreenLed
    {
        get => _greenLed;
        private set
        {
            _greenLed = value;
            OnStateChanged();
        }
    }

    public ImmutableArray<IPEndPoint> RemoteEndpoints { get; private set; } =
        ImmutableArray<IPEndPoint>.Empty;

    public event EventHandler? StateChanged;

    public DeviceConfiguration GetConfiguration()
    {
        return new DeviceConfiguration(
            DeviceId,
            new DeviceAudioFormats(
                DeviceAudioFormat.FromAudioFormat(AudioConstants.AudioFormat),
                DeviceAudioFormat.FromAudioFormat(AudioConstants.AudioFormat)
            ),
            new DeviceDeviceConfiguration("Pieter", "Test Intercom", DeviceId, "develop"),
            $"{localEndPoint.Address}:{localEndPoint.Port}"
        );
    }

    public DeviceState GetState()
    {
        return new DeviceState(
            true,
            true,
            RedLed?.State is DeviceLedState.On or DeviceLedState.Blink,
            GreenLed?.State is DeviceLedState.On or DeviceLedState.Blink,
            IsPlaying,
            IsRecording,
            null,
            null
        );
    }

    public void HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var clientPrefix = $"intercom/client/{DeviceId}/";

        if (!e.ApplicationMessage.Topic.StartsWith(clientPrefix))
        {
            Logger.Error("Unexpected topic {Topic}", e.ApplicationMessage.Topic);
            return;
        }

        var topic = e.ApplicationMessage.Topic[clientPrefix.Length..];

        switch (topic)
        {
            case "set/recording":
                IsRecording = e.ApplicationMessage.ConvertPayloadToString() == "true";
                break;

            case "set/red_led":
                RedLed = JsonSerializer.Deserialize<DeviceLedAction>(
                    e.ApplicationMessage.ConvertPayloadToString(),
                    IntercomJson.Options
                )!;
                break;

            case "set/green_led":
                GreenLed = JsonSerializer.Deserialize<DeviceLedAction>(
                    e.ApplicationMessage.ConvertPayloadToString(),
                    IntercomJson.Options
                )!;
                break;

            case "set/add_endpoint":
                RemoteEndpoints = RemoteEndpoints.Add(
                    IPEndPoint.Parse(e.ApplicationMessage.ConvertPayloadToString()!)
                );
                break;

            case "set/remove_endpoint":
                RemoteEndpoints = RemoteEndpoints.Remove(
                    IPEndPoint.Parse(e.ApplicationMessage.ConvertPayloadToString()!)
                );
                break;
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
