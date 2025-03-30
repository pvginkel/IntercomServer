using IntercomServer.Utils.Audio;

namespace IntercomServer.Utils;

public record DeviceConfiguration(
    string? UniqueId,
    DeviceAudioFormats? AudioFormats,
    DeviceDeviceConfiguration? Device,
    string? Endpoint
);

public record DeviceAudioFormats(DeviceAudioFormat? In, DeviceAudioFormat? Out);

public record DeviceAudioFormat(string? ChannelLayout, int? SampleRate, int? BitRate)
{
    public static DeviceAudioFormat FromAudioFormat(AudioFormat audioFormat)
    {
        return new DeviceAudioFormat(
            audioFormat.ChannelLayout.ToString().ToLower(),
            audioFormat.SampleRate,
            audioFormat.BitRate
        );
    }
}

public record DeviceDeviceConfiguration(string? Manufacturer, string? Model, string? Name);
