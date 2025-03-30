using System.Text.Json.Serialization;

namespace IntercomServer.Utils;

public record DeviceLedAction(
    DeviceLedState State,
    int? Duration = null,
    int? On = null,
    int? Off = null
);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceLedState
{
    [JsonStringEnumMemberName("on")]
    On,

    [JsonStringEnumMemberName("off")]
    Off,

    [JsonStringEnumMemberName("blink")]
    Blink
}
