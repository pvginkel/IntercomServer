using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using IntercomServer.Utils.Audio;
using MQTTnet;

namespace IntercomServer;

internal class CallManager
{
    private static readonly ArrayPool<byte> ArrayPool = ArrayPool<byte>.Shared;

    private IDisposable? _callTimer;
    private readonly List<Device> _devices = [];
    private readonly List<byte[]> _deviceBuffers = [];
    private readonly byte[] _outBuffer = new byte[
        (int)(Constants.AudioFormat.BytesPerSecond * Constants.OutStreamInterval.TotalSeconds)
    ];
    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _lastOffset;
    private readonly IMqttClient _client;
    private readonly AlarmManager _alarmManager;

    public ReadOnlyCollection<Device> InCall { get; }

    public CallManager(IMqttClient client, AlarmManager alarmManager)
    {
        _client = client;
        _alarmManager = alarmManager;

        InCall = _devices.AsReadOnly();
    }

    public void StartCall()
    {
        if (_callTimer != null)
            throw new InvalidOperationException("Call is already running");

        _callTimer = _alarmManager.SetInterval(Constants.OutStreamInterval, SendOutStream);
        _stopwatch.Reset();
        _lastOffset = TimeSpan.Zero;
    }

    private async Task SendOutStream()
    {
        var elapsed = _stopwatch.Elapsed;

        while (elapsed - _lastOffset >= Constants.OutStreamInterval)
        {
            await SendOutStreamChunk();

            _lastOffset += Constants.OutStreamInterval;
        }
    }

    private async Task SendOutStreamChunk()
    {
        // Take audio samples from all devices.

        for (int i = 0; i < _devices.Count; i++)
        {
            _devices[i].AudioBuffer.Take(_deviceBuffers[i]);
        }

        // If there's just one person in the call, they won't hear anything.

        if (_devices.Count < 2)
            return;

        // Mix an audio sample per device (i.e. mix all devices except for
        // the specific device).

        for (int i = 0; i < _devices.Count; i++)
        {
            var hadOne = false;

            for (int j = 0; j < _devices.Count; j++)
            {
                if (i != j)
                {
                    if (!hadOne)
                    {
                        hadOne = true;

                        Array.Copy(_deviceBuffers[j], _outBuffer, _outBuffer.Length);
                    }
                    else
                    {
                        AudioUtils.MixInBuffer(
                            Constants.AudioFormat,
                            _outBuffer,
                            _deviceBuffers[j]
                        );
                    }
                }
            }

            await _client.PublishBinaryAsync(
                $"intercom/{_devices[i].DeviceId}/stream/in",
                _outBuffer
            );
        }
    }

    public void StopCall()
    {
        if (_callTimer == null)
            throw new InvalidOperationException("Call isn't running");

        _callTimer.Dispose();
        _callTimer = null;

        _devices.Clear();
        _deviceBuffers.ForEach(p => ArrayPool.Return(p));
        _deviceBuffers.Clear();
    }

    public void AddDevice(Device device)
    {
        if (_devices.Contains(device))
            throw new ArgumentOutOfRangeException(nameof(device), "Device is already in the call");

        _devices.Add(device);
        _deviceBuffers.Add(ArrayPool.Rent(_outBuffer.Length));

        device.AudioBuffer.Reset();
    }

    public void RemoveDevice(Device device)
    {
        _devices.Remove(device);
    }
}
