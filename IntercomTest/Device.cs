using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using IntercomServer.Utils;
using MQTTnet;
using Serilog;

namespace IntercomTest;

internal class Device(string deviceId, IPEndPoint localEndPoint)
{
    private readonly AudioMixer _audioMixer = new(Constants.AudioFormat, Constants.BufferInterval);

    private readonly SynchronizationContext _synchronizationContext =
        SynchronizationContext.Current!;
    private static readonly ILogger Logger = Log.ForContext<Device>();

    private bool _isPlaying;
    private bool _isRecording;
    private DeviceLedAction? _redLed;
    private DeviceLedAction? _greenLed;

    public string DeviceId { get; } = deviceId;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                OnIsPlayingChanged();
                OnStateChanged();
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (_isRecording != value)
            {
                _isRecording = value;
                OnIsRecordingChanged();
                OnStateChanged();
            }
        }
    }

    public DeviceLedAction? RedLed
    {
        get => _redLed;
        set
        {
            if (_redLed != value)
            {
                _redLed = value;
                OnRedLedChanged();
                OnStateChanged();
            }
        }
    }

    public DeviceLedAction? GreenLed
    {
        get => _greenLed;
        set
        {
            if (_greenLed != value)
            {
                _greenLed = value;
                OnGreenLedChanged();
                OnStateChanged();
            }
        }
    }

    public ImmutableArray<IPEndPoint> RemoteEndpoints { get; private set; } =
        ImmutableArray<IPEndPoint>.Empty;

    public event EventHandler? IsPlayingChanged;
    public event EventHandler? IsRecordingChanged;
    public event EventHandler? RedLedChanged;
    public event EventHandler? GreenLedChanged;
    public event EventHandler<DeviceEndpointEventArgs>? AddedEndpoint;
    public event EventHandler<DeviceEndpointEventArgs>? RemovedEndpoint;
    public event EventHandler? StateChanged;

    public DeviceConfiguration GetConfiguration()
    {
        return new DeviceConfiguration(
            DeviceId,
            new DeviceAudioFormats(
                DeviceAudioFormat.FromAudioFormat(Constants.AudioFormat),
                DeviceAudioFormat.FromAudioFormat(Constants.AudioFormat)
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
                    IntercomClient.JsonSerializerOptions
                )!;
                break;

            case "set/green_led":
                GreenLed = JsonSerializer.Deserialize<DeviceLedAction>(
                    e.ApplicationMessage.ConvertPayloadToString(),
                    IntercomClient.JsonSerializerOptions
                )!;
                break;

            case "set/add_endpoint":
                var endpoint = e.ApplicationMessage.ConvertPayloadToString();

                RemoteEndpoints = RemoteEndpoints.Add(IPEndPoint.Parse(endpoint));
                OnAddedEndpoint(new DeviceEndpointEventArgs(endpoint));
                OnStateChanged();
                break;

            case "set/remove_endpoint":
                endpoint = e.ApplicationMessage.ConvertPayloadToString();

                RemoteEndpoints = RemoteEndpoints.Remove(IPEndPoint.Parse(endpoint));
                OnRemovedEndpoint(new DeviceEndpointEventArgs(endpoint));
                OnStateChanged();
                break;
        }
    }

    public void AppendAudio(IPEndPoint address, byte[] sample)
    {
        _audioMixer.Append(address, sample);

        IsPlaying = true;
    }

    public void Reset()
    {
        _audioMixer.Reset();

        IsPlaying = false;
    }

    public void TakeAudio(byte[] buffer)
    {
        if (!_audioMixer.HasData)
            IsPlaying = false;
        else
            _audioMixer.Take(buffer);
    }

    protected virtual void OnIsPlayingChanged() =>
        _synchronizationContext.Post(_ => IsPlayingChanged?.Invoke(this, EventArgs.Empty), null);

    protected virtual void OnIsRecordingChanged() =>
        _synchronizationContext.Post(_ => IsRecordingChanged?.Invoke(this, EventArgs.Empty), null);

    protected virtual void OnRedLedChanged() =>
        _synchronizationContext.Post(_ => RedLedChanged?.Invoke(this, EventArgs.Empty), null);

    protected virtual void OnGreenLedChanged() =>
        _synchronizationContext.Post(_ => GreenLedChanged?.Invoke(this, EventArgs.Empty), null);

    protected virtual void OnAddedEndpoint(DeviceEndpointEventArgs e) =>
        _synchronizationContext.Post(_ => AddedEndpoint?.Invoke(this, e), null);

    protected virtual void OnRemovedEndpoint(DeviceEndpointEventArgs e) =>
        _synchronizationContext.Post(_ => RemovedEndpoint?.Invoke(this, e), null);

    protected virtual void OnStateChanged() =>
        _synchronizationContext.Post(_ => StateChanged?.Invoke(this, EventArgs.Empty), null);
}
