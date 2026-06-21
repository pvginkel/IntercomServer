namespace IntercomTestWeb;

// MQTT broker connection settings, sourced from the MQTT_* environment variables (D14). Same shape
// as IntercomServer.ServerConfiguration / IntercomTest.ServerConfiguration.
public sealed class ServerConfiguration
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
}
