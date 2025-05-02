using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MQTTnet;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IntercomTest;

internal partial class AECTestWindow
{
    private readonly IMqttClient _client;
    private readonly IntercomUDPServer _udpServer = new(5140);
    private WriteableBitmap? _bitmap;
    private DeviceRef? _listeningDevice;
    private readonly string _udpServerEndpoint;
    private readonly Queue<float> _samples = new();

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
        _fileName.Text = key.GetValue("AEC File Name") as string ?? "";

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
        for (int offset = 0; offset < e.Data.Length; offset += 2)
        {
            _samples.Enqueue(BitConverter.ToInt16(e.Data, offset) / (float)short.MaxValue);
        }

        var samples = new List<float>();

        const int BlockSize = 200;

        while (_samples.Count >= BlockSize)
        {
            float sum = 0;

            for (int i = 0; i < BlockSize; i++)
            {
                sum += Math.Abs(_samples.Dequeue());
            }

            var amp = sum / BlockSize;

            samples.Add(amp);
        }

        Dispatcher.Invoke(() =>
        {
            foreach (var sample in samples)
            {
                DrawSample(sample);
            }
        });
    }

    private void DrawSample(float amplitude)
    {
        if (_bitmap == null)
            return;

        _bitmap.Lock();

        var height = _bitmap.PixelHeight;
        var width = _bitmap.PixelWidth;

        // 1) Scroll left by 1 px: copy pixels [1..W-1] → [0..W-2]
        int stride = _bitmap.BackBufferStride;
        unsafe
        {
            var pBack = (byte*)_bitmap.BackBuffer;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    pBack + (y * stride) + 4, // start at x=1
                    pBack + (y * stride), // dest at x=0
                    stride - 4, // dest buffer size
                    stride - 4
                ); // copy this many bytes
            }
        }

        // 2) Draw new vertical line at x = WIDTH-1
        int midY = height / 2;
        int lineHeight = (int)(amplitude * midY);
        unsafe
        {
            var p = (int*)_bitmap.BackBuffer;
            for (int y = 0; y < height; y++)
            {
                int color = 0;
                if (y >= midY - lineHeight && y <= midY + lineHeight)
                    color = 0x00FF0000; // ARGB red
                p[y * width + (width - 1)] = color;
            }
        }

        _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        _bitmap.Unlock();
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

    private void _selectFile_Click(object sender, RoutedEventArgs e)
    {
        using var form = new OpenFileDialog();

        form.Filter = "Wave Files (*.wav)|*.wav|All Files (*.*)|*.*";
        form.RestoreDirectory = true;

        if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            using var key = App.BaseKey;

            key.SetValue("AEC File Name", form.FileName);

            _fileName.Text = form.FileName;
        }
    }

    private void _playFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_fileName.Text))
            return;

        using var reader = new WaveFileReader(_fileName.Text);

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
}
