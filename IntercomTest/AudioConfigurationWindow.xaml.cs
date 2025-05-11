using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using IntercomServer.Utils;

namespace IntercomTest;

internal partial class AudioConfigurationWindow
{
    private static readonly CultureInfo NumericCulture = CultureInfo.GetCultureInfo("en-US");
    private const string ValidIntegerCharacters = "-0123456789";
    private const string ValidDoubleCharacters = "-.0123456789";

    public AudioConfigurationWindow(AudioConfiguration config)
    {
        InitializeComponent();

        LoadConfiguration(config);
    }

    private void LoadConfiguration(AudioConfiguration config)
    {
        _volumeScaleLow.Text = config.VolumeScaleLow.ToString("G3", NumericCulture);
        _volumeScaleHigh.Text = config.VolumeScaleHigh.ToString("G3", NumericCulture);
        _enableAudioProcessing.IsChecked = config.EnableAudioProcessing;
        _audioBufferMs.Text = config.AudioBufferMs.ToString(NumericCulture);
        _microphoneGainBits.Text = config.MicrophoneGainBits.ToString(NumericCulture);
        _recordingAutoVolumeEnabled.IsChecked = config.RecordingAutoVolumeEnabled;
        _recordingSmoothingFactor.Text = config.RecordingSmoothingFactor.ToString(
            "G3",
            NumericCulture
        );
        _playbackAutoVolumeEnabled.IsChecked = config.PlaybackAutoVolumeEnabled;
        _playbackTargetDb.Text = config.PlaybackTargetDb.ToString("G3", NumericCulture);

        UpdateEnabled();
    }

    private void _ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void _cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PreviewDoubleTextBox(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !ValidDoubleCharacters.Contains(e.Text);
    }

    private void PreviewIntegerTextBox(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !ValidIntegerCharacters.Contains(e.Text);
    }

    private void InputChanged(object sender, EventArgs e)
    {
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        _ok.IsEnabled = _copy.IsEnabled = GetConfiguration() != null;
    }

    public AudioConfiguration? GetConfiguration()
    {
        if (
            double.TryParse(_volumeScaleLow.Text, NumericCulture, out var volumeScaleLow)
            && double.TryParse(_volumeScaleHigh.Text, NumericCulture, out var volumeScaleHigh)
            && int.TryParse(_audioBufferMs.Text, NumericCulture, out var audioBufferMs)
            && int.TryParse(_microphoneGainBits.Text, NumericCulture, out var microphoneGainBits)
            && double.TryParse(
                _recordingSmoothingFactor.Text,
                NumericCulture,
                out var recordingSmoothingFactor
            )
            && double.TryParse(_playbackTargetDb.Text, NumericCulture, out var playbackTargetDb)
        )
        {
            return new AudioConfiguration(
                volumeScaleLow,
                volumeScaleHigh,
                _enableAudioProcessing.IsChecked.GetValueOrDefault(),
                audioBufferMs,
                microphoneGainBits,
                _recordingAutoVolumeEnabled.IsChecked.GetValueOrDefault(),
                recordingSmoothingFactor,
                _playbackAutoVolumeEnabled.IsChecked.GetValueOrDefault(),
                playbackTargetDb
            );
        }

        return null;
    }

    private void _copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(
            JsonSerializer.Serialize(GetConfiguration(), IntercomClient.JsonSerializerOptions)
        );
    }

    private void _paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AudioConfiguration>(
                Clipboard.GetText(),
                IntercomClient.JsonSerializerOptions
            )!;

            LoadConfiguration(config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error: {ex.Message}", "Failed to load configuration");
        }
    }
}
