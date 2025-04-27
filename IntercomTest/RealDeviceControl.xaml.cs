using System.Windows;
using System.Windows.Media;
using IntercomServer.Utils;
using static System.Windows.Forms.AxHost;

namespace IntercomTest;

internal partial class RealDeviceControl
{
    public string DeviceId { get; }

    public event EventHandler? RemoveClicked;

    public event EventHandler<double>? VolumeChanged;

    public RealDeviceControl(string deviceId)
    {
        DeviceId = deviceId;

        InitializeComponent();

        _groupBox.Header = deviceId;
    }

    public void SetState(DeviceState state)
    {
        _innerGrid.IsEnabled = state.Online.GetValueOrDefault();

        _playbackVolume.Value = state.Volume.GetValueOrDefault();
        _redLed.Fill = state.RedLed.GetValueOrDefault() ? Brushes.Red : Brushes.White;
        _greenLed.Fill = state.GreenLed.GetValueOrDefault() ? Brushes.Green : Brushes.White;
        _enabled.IsChecked = state.Enabled.GetValueOrDefault();
        _recording.IsChecked = state.Recording.GetValueOrDefault();
        _playing.IsChecked = state.Playing.GetValueOrDefault();
    }

    public void SetConfiguration(DeviceConfiguration configuration)
    {
        _name.Content = configuration.Device?.Name;
    }

    private void _playbackVolume_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e
    )
    {
        var value = Math.Round(e.NewValue, 2);

        if (_playbackVolumeLabel != null)
            _playbackVolumeLabel.Content = $"{value} Db";

        if (!_playbackVolume.IsTracking)
            OnVolumeChanged(value);
    }

    private void _remove_Click(object sender, RoutedEventArgs e) => OnRemoveClicked();

    protected virtual void OnRemoveClicked() => RemoveClicked?.Invoke(this, EventArgs.Empty);

    protected virtual void OnVolumeChanged(double e) => VolumeChanged?.Invoke(this, e);
}
