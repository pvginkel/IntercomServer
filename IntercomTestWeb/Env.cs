namespace IntercomTestWeb;

// Small helpers for reading the environment-variable configuration (MQTT_*, HTTP_PORT, DATA_DIR),
// mirroring the convention used by IntercomServer.
public static class Env
{
    public static string? Str(string name) => Environment.GetEnvironmentVariable(name);

    public static int Int(string name, int @default) =>
        int.TryParse(Str(name), out var value) ? value : @default;

    public static int? IntOrNull(string name) =>
        string.IsNullOrEmpty(Str(name)) ? null : int.Parse(Str(name)!);
}
