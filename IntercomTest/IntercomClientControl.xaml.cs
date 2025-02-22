using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using IntercomServer.Utils;
using NAudio.CoreAudioApi;
using NAudio.Mixer;
using NAudio.Wave;
using Serilog;

namespace IntercomTest;

internal partial class IntercomClientControl
{
    private WasapiCapture? _waveIn;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private WasapiOut? _waveOut;

    private static readonly ILogger Logger = Log.ForContext<IntercomClientControl>();

    public IntercomClient IntercomClient { get; }
    public Device Device { get; }

    public event EventHandler? RemoveClicked;

    public IntercomClientControl(
        IntercomClientConfiguration clientConfiguration,
        ServerConfiguration serverConfiguration
    )
    {
        InitializeComponent();

        Device = new Device(clientConfiguration.DeviceId);
        IntercomClient = new IntercomClient(Device, serverConfiguration);

        Device.IsPlayingChanged += Device_IsPlayingChanged;
        Device.IsRecordingChanged += Device_IsRecordingChanged;
        Device.GreenLedChanged += (_, _) =>
            TaskUtils.Run(() => UpdateLed(_greenLed, () => Device.GreenLed, Brushes.Green));
        Device.RedLedChanged += (_, _) =>
            TaskUtils.Run(() => UpdateLed(_redLed, () => Device.RedLed, Brushes.Red));

        _groupBox.Header = Device.DeviceId;

        _recordingDevice.Items.Add("");

        var enumerator = new MMDeviceEnumerator();

        foreach (
            var wasapi in enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture,
                NAudio.CoreAudioApi.DeviceState.Active
            )
        )
        {
            _recordingDevice.Items.Add(wasapi.FriendlyName);
        }

        _playbackDevice.Items.Add("");

        foreach (
            var wasapi in enumerator.EnumerateAudioEndPoints(
                DataFlow.Render,
                NAudio.CoreAudioApi.DeviceState.Active
            )
        )
        {
            _playbackDevice.Items.Add(wasapi.FriendlyName);
        }

        _recordingDevice.SelectedItem = clientConfiguration.RecordingDevice ?? "";
        _playbackDevice.SelectedItem = clientConfiguration.PlaybackDevice ?? "";
    }

    private async Task UpdateLed(Ellipse led, Func<DeviceLedAction?> func, Brush brush)
    {
        var action = func();

        switch (action?.State)
        {
            case null:
                break;

            case DeviceLedState.On:
                led.Fill = brush;

                if (action.Duration.HasValue)
                {
                    await Task.Delay(action.Duration.Value);

                    if (action == func())
                        led.Fill = Brushes.White;
                }
                break;

            case DeviceLedState.Off:
                led.Fill = Brushes.White;
                return;

            case DeviceLedState.Blink:
                var stopwatch = Stopwatch.StartNew();

                while (action == func())
                {
                    led.Fill = brush;

                    await Task.Delay(action.On!.Value);

                    if (action != func())
                        break;

                    led.Fill = Brushes.White;

                    await Task.Delay(action.Off!.Value);

                    if (
                        action.Duration.HasValue
                        && stopwatch.ElapsedMilliseconds >= action.Duration.Value
                    )
                        return;
                }
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void Device_IsPlayingChanged(object? sender, EventArgs e)
    {
        _playing.IsChecked = Device.IsPlaying;

        if (Device.IsPlaying)
            StartPlayback();
        else
            StopPlayback();
    }

    private void StartPlayback()
    {
        var device = GetMMDevice(_playbackDevice.Text, DataFlow.Render);
        if (device == null)
            return;

        _bufferedWaveProvider = new BufferedWaveProvider(
            new WaveFormat(
                Constants.AudioFormat.SampleRate,
                Constants.AudioFormat.BitRate,
                Constants.AudioFormat.ChannelCount
            )
        );

        _waveOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 10);
        _waveOut.Init(_bufferedWaveProvider);
        _waveOut.Play();

        Task.Run(PipeAudio);
    }

    private MMDevice? GetMMDevice(string friendlyName, DataFlow dataFlow)
    {
        var enumerator = new MMDeviceEnumerator();

        foreach (
            var wasapi in enumerator.EnumerateAudioEndPoints(
                dataFlow,
                NAudio.CoreAudioApi.DeviceState.Active
            )
        )
        {
            if (friendlyName == wasapi.FriendlyName)
                return wasapi;
        }

        return null;
    }

    private async Task PipeAudio()
    {
        var stopwatch = Stopwatch.StartNew();
        var elapsed = TimeSpan.Zero;
        var bufferSize = TimeSpan.FromMilliseconds(20);
        var buffer = new byte[
            (int)(Constants.AudioFormat.BytesPerSecond * bufferSize.TotalSeconds)
        ];

        while (_waveOut?.PlaybackState == PlaybackState.Playing)
        {
            while (stopwatch.Elapsed > elapsed + bufferSize)
            {
                Device.TakeAudio(buffer);

                _bufferedWaveProvider?.AddSamples(buffer, 0, buffer.Length);

                elapsed += bufferSize;
            }

            await Task.Delay(bufferSize);
        }
    }

    private void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _bufferedWaveProvider = null;
    }

    private void Device_IsRecordingChanged(object? sender, EventArgs e)
    {
        _recording.IsChecked = Device.IsRecording;

        if (Device.IsRecording)
            StartRecording();
        else
            StopRecording();
    }

    private void StartRecording()
    {
        var device = GetMMDevice(_recordingDevice.Text, DataFlow.Capture);
        if (device == null)
            return;

        _waveIn = new WasapiCapture(device)
        {
            WaveFormat = new WaveFormat(
                Constants.AudioFormat.SampleRate,
                Constants.AudioFormat.BitRate,
                Constants.AudioFormat.ChannelCount
            )
        };
        _waveIn.DataAvailable += _waveIn_DataAvailable;
        _waveIn.RecordingStopped += _waveIn_RecordingStopped;

        _waveIn.StartRecording();
    }

    private static readonly int MicrophoneGainSampleCount = Constants.AudioFormat.SampleRate * 2;
    private int _microphoneGainSampleCount;
    private double _microphoneGainSample;

    private async void _waveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                var sample = e.Buffer[i] << 8 | e.Buffer[i + 1];
                double normalizedSample = (sample - 32768) / 32768.0;
                _microphoneGainSample += normalizedSample * normalizedSample;
                _microphoneGainSampleCount++;

                if (_microphoneGainSampleCount >= MicrophoneGainSampleCount)
                {
                    var rms = Math.Sqrt(_microphoneGainSample / _microphoneGainSampleCount);

                    Dispatcher.BeginInvoke(() =>
                    {
                        var volumeControl = GetMMDevice(
                            _recordingDevice.Text,
                            DataFlow.Capture
                        )!.AudioEndpointVolume;

                        var volume = volumeControl.MasterVolumeLevelScalar;

                        float newVolume;
                        if (rms > 0.95)
                            newVolume = Math.Min(1.0f, volume * 1.1f);
                        else if (rms < 0.6)
                            newVolume = Math.Max(0.3f, volume * 0.9f);
                        else
                            return;

                        Logger.Information(
                            "RMS {RMS}, changed volume from {OldVolume} to {NewVolume}",
                            rms,
                            volume,
                            newVolume
                        );

                        volumeControl.MasterVolumeLevelScalar = newVolume;
                    });

                    _microphoneGainSample = 0;
                    _microphoneGainSampleCount = 0;
                }
            }

            await IntercomClient.SendAudio(e.Buffer.Take(e.BytesRecorded));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to send sample");
        }
    }

    private void _waveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        _waveIn?.Dispose();
        _waveIn = null;
    }

    private void StopRecording()
    {
        _waveIn?.StopRecording();
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await IntercomClient.Connect();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect the client");
        }
    }

    public IntercomClientConfiguration GetConfiguration()
    {
        return new IntercomClientConfiguration(
            Device.DeviceId,
            _recordingDevice.Text,
            _playbackDevice.Text
        );
    }

    private void _remove_Click(object sender, RoutedEventArgs e)
    {
        OnRemoveClicked();
    }

    protected virtual void OnRemoveClicked() => RemoveClicked?.Invoke(this, EventArgs.Empty);

    private async void _clickButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await IntercomClient.SendAction(DeviceAction.Click);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to send action");
        }
    }

    private async void _longClickButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await IntercomClient.SendAction(DeviceAction.LongClick);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to send action");
        }
    }
}
