using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IntercomServer.Utils;
using NAudio.CoreAudioApi;
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

    private void UpdateEnabled()
    {
        _recording.IsChecked = Device.IsRecording;
        _recordingDevice.IsEnabled = !Device.IsRecording;
        _recordingVolume.IsEnabled = _recordingDevice.Text.Length > 0 && Device.IsRecording;

        _playing.IsChecked = Device.IsPlaying;
        _playbackDevice.IsEnabled = !Device.IsPlaying;
        _playbackVolume.IsEnabled = _playbackDevice.Text.Length > 0 && Device.IsPlaying;
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
        if (Device.IsPlaying)
            StartPlayback();
        else
            StopPlayback();

        UpdateEnabled();
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

        _playbackVolume.Value = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100;

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
        if (Device.IsRecording)
            StartRecording();
        else
            StopRecording();

        UpdateEnabled();
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

        _recordingVolume.Value = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
    }

    private async void _waveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
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

    private void _recordingDevice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEnabled();

    private void _playbackDevice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEnabled();

    private void _recordingVolume_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e
    )
    {
        GetMMDevice(
            _recordingDevice.Text,
            DataFlow.Capture
        )!.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(e.NewValue / 100.0);
    }

    private void _playbackVolume_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e
    )
    {
        GetMMDevice(
            _playbackDevice.Text,
            DataFlow.Render
        )!.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(e.NewValue / 100.0);
    }
}
