using System.Diagnostics;
using System.Windows;
using IntercomServer.Utils;
using NAudio.Wave;
using Serilog;

namespace IntercomTest;

internal partial class IntercomClientControl
{
    private WaveInEvent? _waveIn;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private WaveOutEvent? _waveOut;

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

        _groupBox.Header = Device.DeviceId;

        _recordingDevice.Items.Add("");

        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var deviceInfo = WaveIn.GetCapabilities(i);

            _recordingDevice.Items.Add(deviceInfo.ProductName);
        }

        _playbackDevice.Items.Add("");

        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var deviceInfo = WaveOut.GetCapabilities(i);

            _playbackDevice.Items.Add(deviceInfo.ProductName);
        }

        _recordingDevice.SelectedItem = clientConfiguration.RecordingDevice ?? "";
        _playbackDevice.SelectedItem = clientConfiguration.PlaybackDevice ?? "";
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
        int deviceNumber = -1;

        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            if (WaveOut.GetCapabilities(i).ProductName == _playbackDevice.Text)
                deviceNumber = i;
        }

        if (deviceNumber == -1)
            return;

        _bufferedWaveProvider = new BufferedWaveProvider(
            new WaveFormat(
                Constants.AudioFormat.SampleRate,
                Constants.AudioFormat.BitRate,
                Constants.AudioFormat.ChannelCount
            )
        );

        _waveOut = new WaveOutEvent();
        _waveOut.DeviceNumber = deviceNumber;
        _waveOut.Init(_bufferedWaveProvider);
        _waveOut.Play();

        Task.Run(PipeAudio);
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
        var deviceNumber = -1;

        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            if (WaveIn.GetCapabilities(i).ProductName == _recordingDevice.Text)
                deviceNumber = i;
        }

        if (deviceNumber == -1)
            return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
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
}
