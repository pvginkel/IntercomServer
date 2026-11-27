using System.Text.Json.Nodes;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The commands the server publishes on a device's <c>set/…</c> topics, as the firmware reads
/// them: an LED action and the recording flag as JSON, a peer's endpoint as the plain
/// <c>host:port</c> its configuration advertises. A test lists what a device is sent, in order,
/// with <see cref="TestDevice.ExpectCommandsAsync"/>.
/// </summary>
internal static class Commands
{
    public static ExpectedCommand RedLed(string json) => new("set/red_led", json, IsJson: true);

    public static ExpectedCommand GreenLed(string json) => new("set/green_led", json, IsJson: true);

    public static ExpectedCommand Recording(bool on) =>
        new("set/recording", on ? "true" : "false", IsJson: true);

    public static ExpectedCommand AddEndpoint(string endpoint) =>
        new("set/add_endpoint", endpoint, IsJson: false);

    public static ExpectedCommand RemoveEndpoint(string endpoint) =>
        new("set/remove_endpoint", endpoint, IsJson: false);
}

/// <summary>The LED actions the server sends, as their JSON on the wire.</summary>
internal static class Led
{
    public const string On = """{"state":"on"}""";

    public const string Off = """{"state":"off"}""";

    /// <summary>A call being placed: the caller's red LED, and each rung device's.</summary>
    public const string Calling = """{"state":"blink","on":500,"off":500}""";

    /// <summary>The caller's red LED when a click found nobody to ring.</summary>
    public const string CallNotPossible = """{"state":"on","duration":1000}""";

    /// <summary>The caller's red LED once its call was rejected.</summary>
    public const string Rejected = """{"state":"blink","duration":2000,"on":200,"off":200}""";
}

/// <summary>
/// A command a test expects: its subtopic, and its payload, compared as JSON whatever its key
/// order and spacing, or as text.
/// </summary>
internal sealed record ExpectedCommand(string Subtopic, string Payload, bool IsJson)
{
    public bool Matches(DeviceCommand command) =>
        command.Subtopic == Subtopic
        && (
            IsJson
                ? JsonNode.DeepEquals(JsonNode.Parse(Payload), JsonNode.Parse(command.Payload!))
                : command.Payload == Payload
        );

    public override string ToString() => $"{Subtopic} {Payload}";
}
