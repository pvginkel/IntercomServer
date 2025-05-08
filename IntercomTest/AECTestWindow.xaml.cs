using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IntercomTest.SoundRendering;
using MQTTnet;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace IntercomTest;

internal partial class AECTestWindow
{
    private enum State
    {
        None,
        Recording,
        Playing
    }

    private const string SampleFileName = "AEC Sample.wav";

    private readonly IMqttClient _client;
    private readonly IntercomUDPServer _udpServer = new(5140);
    private WriteableBitmap? _bitmap;
    private ISoundRenderer? _renderer;
    private DeviceRef? _listeningDevice;
    private readonly string _udpServerEndpoint;
    private WaveFileWriter? _writer;
    private State _state = State.None;
    private int _nextIndex;

    public AECTestWindow(IMqttClient client, List<DeviceRef> devices)
    {
        _client = client;

        _client.ApplicationMessageReceivedAsync += _client_ApplicationMessageReceivedAsync;

        InitializeComponent();

        _udpServerEndpoint =
            $"{NetworkUtils.GetNetworkIPAddresses().Single()}:{_udpServer.LocalEndPoint.Port}";

        _udpServer.Data += _udpServer_Data;

        using var key = App.BaseKey;

        var lastDevice = key.GetValue("AEC Device") as string;
        _spectrogram.IsChecked = (key.GetValue("AEC Spectrogram") as int?).GetValueOrDefault() != 0;

        _device.Items.Add("");

        foreach (
            var device in devices.OrderBy(
                p => p.Configuration.Device!.Name,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            _device.Items.Add(new ComboBoxItem(device.Configuration.Device!.Name!, device));

            if (device.DeviceId == lastDevice)
                _device.SelectedIndex = _device.Items.Count - 1;
        }

        Dispatcher.BeginInvoke(RecreateBitmap, DispatcherPriority.Render);

        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        _clearSample.IsEnabled = _playSample.IsEnabled =
            File.Exists(SampleFileName) && _state == State.None && _listeningDevice != null;
        _recordSample.IsEnabled = _listeningDevice != null && _state == State.None;
        _recordSample.Visibility =
            _state == State.Recording ? Visibility.Collapsed : Visibility.Visible;
        _stopRecordingSample.Visibility =
            _state == State.Recording ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task _client_ApplicationMessageReceivedAsync(
        MqttApplicationMessageReceivedEventArgs arg
    )
    {
        if (_listeningDevice == null)
            return;

        var prefix = $"intercom/client/{_listeningDevice.DeviceId}/";
        if (!arg.ApplicationMessage.Topic.StartsWith(prefix))
            return;

        switch (arg.ApplicationMessage.Topic[prefix.Length..])
        {
            case "configuration":
                // Assume a configuration message means a restart of the device.
                await ConfigureDevice(_listeningDevice.DeviceId, true);
                break;
        }
    }

    private void _udpServer_Data(object? sender, IntercomUDPDataEventArgs e)
    {
        var index = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(e.Data));

        if (index != _nextIndex)
            Log.Information("Expected index {NextIndex} got {Index}", _nextIndex, index);
        _nextIndex = index + 1;

        _writer?.Write(e.Data.AsSpan(4));

        if (_renderer == null)
            return;

        Dispatcher.Invoke(() =>
        {
            _renderer.AddData(e.Data);
        });
    }

    private void RecreateBitmap()
    {
        if (_waveImageContainer.ActualWidth == 0)
            return;

        _bitmap = new WriteableBitmap(
            (int)_waveImageContainer.ActualWidth,
            (int)_waveImageContainer.ActualHeight,
            96,
            96,
            PixelFormats.Bgr32,
            null
        );

        _waveImage.Source = _bitmap;

        _renderer = _spectrogram.IsChecked.GetValueOrDefault()
            ? new SpectrogramRenderer(_bitmap)
            : new WaveRenderer(_bitmap);
    }

    private async void BaseWindow_Closed(object sender, EventArgs e)
    {
        if (_listeningDevice != null)
            await ConfigureDevice(_listeningDevice.DeviceId, false);

        _udpServer.Dispose();

        _client.ApplicationMessageReceivedAsync -= _client_ApplicationMessageReceivedAsync;
    }

    private async void _device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RecreateBitmap();

        if (_listeningDevice != null)
            await ConfigureDevice(_listeningDevice.DeviceId, false);

        _listeningDevice = (_device.SelectedItem as ComboBoxItem)?.Tag as DeviceRef;

        using var key = App.BaseKey;

        key.SetValue("AEC Device", _listeningDevice?.DeviceId ?? "");

        if (_listeningDevice != null)
            await ConfigureDevice(_listeningDevice.DeviceId, true);

        UpdateEnabled();
    }

    private async Task ConfigureDevice(string deviceId, bool enable)
    {
        await _client.PublishStringAsync(
            $"intercom/client/{deviceId}/set/{(enable ? "add" : "remove")}_endpoint",
            _udpServerEndpoint
        );
        await _client.PublishStringAsync(
            $"intercom/client/{deviceId}/set/recording",
            enable ? "true" : "false"
        );
    }

    private void _waveImageContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecreateBitmap();
    }

    private void _playSample_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(SampleFileName))
            return;

        _state = State.Playing;

        UpdateEnabled();

        using var reader = new WaveFileReader(SampleFileName);

        var sampleProvider = reader.ToSampleProvider();

        if (sampleProvider.WaveFormat.SampleRate != Constants.AudioFormat.SampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(
                sampleProvider,
                Constants.AudioFormat.SampleRate
            );
        }

        if (sampleProvider.WaveFormat.Channels != Constants.AudioFormat.ChannelCount)
        {
            sampleProvider = Constants.AudioFormat.ChannelCount switch
            {
                1 => new StereoToMonoSampleProvider(sampleProvider),
                2 => new MonoToStereoSampleProvider(sampleProvider),
                _ => sampleProvider
            };
        }

        var waveProvider = new SampleToWaveProvider16(sampleProvider);

        var buffer = new byte[waveProvider.WaveFormat.AverageBytesPerSecond];

        var result = new MemoryStream();

        int read;
        while ((read = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, read);
        }

        result.Position = 0;

        TaskUtils.Run(() => RunPlayback(result));
    }

    private async Task RunPlayback(Stream stream)
    {
        string fileName = Path.GetFullPath("AEC Output.wav");

        Log.Information("Start dumping audio data to {FileName}", fileName);

        _writer = new WaveFileWriter(fileName, new WaveFormat(16000, 16, 1));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var sent = TimeSpan.Zero;
            var buffer = new byte[
                (int)(Constants.AudioFormat.BytesPerSecond * Constants.AudioChunkSize.TotalSeconds)
            ];

            stream.Position = 0;
            int index = 0;

            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);

                if (read == 0)
                    return;

                Send(
                    ref index,
                    IPEndPoint.Parse(_listeningDevice!.Configuration!.Endpoint!),
                    buffer.AsSpan(0, read)
                );

                sent += TimeSpan.FromSeconds((double)read / Constants.AudioFormat.BytesPerSecond);

                var delay = sent - stopwatch.Elapsed;
                if (delay.Ticks > 0)
                    await Task.Delay(delay);
            }
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            _writer?.Dispose();
            _writer = null;

            _state = State.None;

            Dispatcher.BeginInvoke(UpdateEnabled);
        }
    }

    private void Send(ref int index, IPEndPoint endPoint, Span<byte> data)
    {
        const int maxDataSize =
            1472 /* max safe data size assuming an MTU of 1500 */
            - 4 /* packet index */
        ;

        for (int offset = 0; offset < data.Length; offset += maxDataSize)
        {
            int len = Math.Min(data.Length - offset, maxDataSize);

            var packetIndex = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(index++));
            var buffer = new byte[len + 4];

            Array.Copy(packetIndex, buffer, 4);
            data.Slice(offset, len).CopyTo(buffer.AsSpan(4, len));

            _udpServer.Send(endPoint, buffer);
        }
    }

    private void _spectrogram_Checked(object sender, RoutedEventArgs e)
    {
        using var key = App.BaseKey;

        key.SetValue("AEC Spectrogram", _spectrogram.IsChecked.GetValueOrDefault() ? 1 : 0);

        RecreateBitmap();
    }

    private void _recordSample_Click(object sender, RoutedEventArgs e)
    {
        _state = State.Recording;
        _nextIndex = 0;

        UpdateEnabled();

        _writer = new WaveFileWriter(SampleFileName, new WaveFormat(16000, 16, 1));
    }

    private void _clearSample_Click(object sender, RoutedEventArgs e)
    {
        File.Delete(SampleFileName);

        UpdateEnabled();
    }

    private void _stopRecordingSample_Click(object sender, RoutedEventArgs e)
    {
        _writer?.Dispose();
        _writer = null;

        _state = State.None;

        UpdateEnabled();
    }
}
