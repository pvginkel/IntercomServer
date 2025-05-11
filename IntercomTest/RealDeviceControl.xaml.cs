using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IntercomServer.Utils;

namespace IntercomTest;

internal partial class RealDeviceControl
{
    private readonly string _latestFirmwareVersion;
    private int _suppressUpdate;
    private AudioConfiguration? _audioConfig;

    public DeviceConfiguration? Configuration { get; private set; }
    public string DeviceId { get; }

    public event EventHandler? RemoveClicked;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler? IdentifyClicked;
    public event EventHandler? RestartClicked;
    public event EventHandler<EnabledEventArgs>? EnabledChanged;
    public event EventHandler<AudioConfigurationEventArgs>? AudioConfigChanged;

    public RealDeviceControl(string deviceId, string latestFirmwareVersion)
    {
        _latestFirmwareVersion = latestFirmwareVersion;
        DeviceId = deviceId;

        InitializeComponent();

        _groupBox.Header = deviceId;

        UpdateEnabled();
    }

    public void SetState(DeviceState state)
    {
        _audioConfig = state.AudioConfig;

        _suppressUpdate++;
        try
        {
            _innerGrid.IsEnabled = state.Online.GetValueOrDefault();
            _playbackVolume.Value = state.Volume.GetValueOrDefault();
            _redLed.Fill = state.RedLed.GetValueOrDefault() ? Brushes.Red : Brushes.White;
            _greenLed.Fill = state.GreenLed.GetValueOrDefault() ? Brushes.Green : Brushes.White;
            _enabled.IsChecked = state.Enabled.GetValueOrDefault();
            _recording.IsChecked = state.Recording.GetValueOrDefault();
            _playing.IsChecked = state.Playing.GetValueOrDefault();
        }
        finally
        {
            _suppressUpdate--;
        }

        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        _configure.IsEnabled = _audioConfig != null;
    }

    public void SetConfiguration(DeviceConfiguration configuration)
    {
        Configuration = configuration;

        _name.Content = configuration.Device?.Name;

        var firmwareVersion = configuration.Device?.FirmwareVersion;
        if (firmwareVersion == _latestFirmwareVersion)
            firmwareVersion += " (up to date)";
        else
            firmwareVersion += $" (latest {_latestFirmwareVersion})";

        _firmwareVersion.Content = firmwareVersion;
    }

    private void _playbackVolume_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e
    )
    {
        if (_suppressUpdate > 0)
            return;

        var value = Math.Round(e.NewValue, 2);

        if (!_playbackVolume.IsTracking)
            OnVolumeChanged(value);
    }

    private void _remove_Click(object sender, RoutedEventArgs e) => OnRemoveClicked();

    private void _identify_Click(object sender, RoutedEventArgs e) => OnIdentifyClicked();

    private async void _restart_Click(object sender, RoutedEventArgs e)
    {
        _restart.IsEnabled = false;

        OnRestartClicked();

        await Task.Delay(TimeSpan.FromSeconds(1));

        _restart.IsEnabled = true;
    }

    protected virtual void OnRemoveClicked() => RemoveClicked?.Invoke(this, EventArgs.Empty);

    protected virtual void OnVolumeChanged(double e) => VolumeChanged?.Invoke(this, e);

    protected virtual void OnIdentifyClicked() => IdentifyClicked?.Invoke(this, EventArgs.Empty);

    protected virtual void OnRestartClicked() => RestartClicked?.Invoke(this, EventArgs.Empty);

    private void _enabled_Checked(object sender, RoutedEventArgs e) =>
        OnEnabledChanged(new EnabledEventArgs(_enabled.IsChecked.GetValueOrDefault()));

    protected virtual void OnEnabledChanged(EnabledEventArgs e) => EnabledChanged?.Invoke(this, e);

    private void _configure_Click(object sender, RoutedEventArgs e)
    {
        var owner = this.GetWindow()!;

        var window = new AudioConfigurationWindow(_audioConfig!)
        {
            Owner = owner,
            Icon = owner.Icon
        };

        if (window.ShowDialog().GetValueOrDefault())
        {
            var config = window.GetConfiguration();

            if (config != null)
                OnAudioConfigChanged(new AudioConfigurationEventArgs(config));
        }
    }

    protected virtual void OnAudioConfigChanged(AudioConfigurationEventArgs e)
    {
        AudioConfigChanged?.Invoke(this, e);
    }
}
