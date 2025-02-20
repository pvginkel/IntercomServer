using System.Buffers;
using System.Text.Json;
using IntercomServer.Utils;
using IntercomServer.Utils.Audio;
using MQTTnet;

namespace IntercomTest;

internal class Device(string deviceId)
{
    private readonly AudioBuffer _audioBuffer =
        new(Constants.AudioFormat, TimeSpan.Zero, Constants.AudioTrailBuffer);

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
            }
        }
    }

    public event EventHandler? IsPlayingChanged;
    public event EventHandler? IsRecordingChanged;
    public event EventHandler? RedLedChanged;
    public event EventHandler? GreenLedChanged;

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
            IsRecording
        );
    }

    public void HandleMessage(string topic, MqttApplicationMessageReceivedEventArgs e)
    {
        switch (topic)
        {
            case "stream/in":
                AppendAudio(e.ApplicationMessage.Payload);
                break;

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
        }
    }

    public void Reset()
    {
        _audioBuffer.Reset();

        IsPlaying = false;
    }

    private void AppendAudio(ReadOnlySequence<byte> sample)
    {
        _audioBuffer.Append(sample);

        if (!IsPlaying && _audioBuffer.BufferUsedTime >= Constants.BufferInterval)
            IsPlaying = true;
    }

    public int TakeAudio(byte[] buffer)
    {
        if (_audioBuffer.BufferUsed == 0)
        {
            IsPlaying = false;
            return 0;
        }

        return _audioBuffer.Take(buffer);
    }

    protected virtual void OnIsPlayingChanged() => IsPlayingChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnIsRecordingChanged() =>
        IsRecordingChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnRedLedChanged() => RedLedChanged?.Invoke(this, EventArgs.Empty);

    protected virtual void OnGreenLedChanged() => GreenLedChanged?.Invoke(this, EventArgs.Empty);
}
