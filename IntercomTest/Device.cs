using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using IntercomServer.Utils;
using MQTTnet;

namespace IntercomTest;

internal class Device(string deviceId)
{
    private readonly AudioMixer _audioMixer = new(Constants.AudioFormat, Constants.BufferInterval);

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

    public ImmutableArray<string> SubscribedStreams { get; private set; } =
        ImmutableArray<string>.Empty;

    public event EventHandler? IsPlayingChanged;
    public event EventHandler? IsRecordingChanged;
    public event EventHandler? RedLedChanged;
    public event EventHandler? GreenLedChanged;
    public event EventHandler<DeviceStreamEventArgs>? SubscribedStream;
    public event EventHandler<DeviceStreamEventArgs>? UnsubscribedStream;
    public event EventHandler? StateChanged;

    public DeviceConfiguration GetConfiguration()
    {
        return new DeviceConfiguration(
            DeviceId,
            new DeviceAudioFormats(
                DeviceAudioFormat.FromAudioFormat(Constants.AudioFormat),
                DeviceAudioFormat.FromAudioFormat(Constants.AudioFormat)
            ),
            new DeviceDeviceConfiguration("Pieter", "Test Intercom", DeviceId)
        );
    }

    public DeviceState GetState()
    {
        return new DeviceState(
            true,
            true,
            RedLed?.State == DeviceLedState.On,
            GreenLed?.State == DeviceLedState.On,
            IsPlaying,
            IsRecording,
            SubscribedStreams
        );
    }

    public void HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        if (SubscribedStreams.Contains(e.ApplicationMessage.Topic))
        {
            AppendAudio(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload);
            return;
        }

        var clientPrefix = $"intercom/client/{DeviceId}/";
        if (!e.ApplicationMessage.Topic.StartsWith(clientPrefix))
            throw new InvalidOperationException("Unexpected topic name");

        var topic = e.ApplicationMessage.Topic[clientPrefix.Length..];

        switch (topic)
        {
            case "set/recording":
                IsRecording = e.ApplicationMessage.ConvertPayloadToString() == "true";
                break;

            case "set/red_led":
                RedLed = JsonSerializer.Deserialize<DeviceLedAction>(
                    e.ApplicationMessage.ConvertPayloadToString()
                )!;
                break;

            case "set/green_led":
                GreenLed = JsonSerializer.Deserialize<DeviceLedAction>(
                    e.ApplicationMessage.ConvertPayloadToString()
                )!;
                break;

            case "set/subscribe_stream":
                var stream = e.ApplicationMessage.ConvertPayloadToString();

                SubscribedStreams = SubscribedStreams.Add(stream);
                OnSubscribedStream(new DeviceStreamEventArgs(stream));
                OnStateChanged();
                break;

            case "set/unsubscribe_stream":
                stream = e.ApplicationMessage.ConvertPayloadToString();

                SubscribedStreams = SubscribedStreams.Remove(stream);
                OnUnsubscribedStream(new DeviceStreamEventArgs(stream));
                OnStateChanged();
                break;
        }
    }

    private void AppendAudio(string topic, ReadOnlySequence<byte> sample)
    {
        _audioMixer.Append(topic, sample);

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

    protected virtual void OnIsPlayingChanged() => IsPlayingChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnIsRecordingChanged() =>
        IsRecordingChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnRedLedChanged() => RedLedChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnGreenLedChanged() => GreenLedChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnSubscribedStream(DeviceStreamEventArgs e) =>
        SubscribedStream?.Invoke(this, e);

    protected virtual void OnUnsubscribedStream(DeviceStreamEventArgs e) =>
        UnsubscribedStream?.Invoke(this, e);

    protected virtual void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
