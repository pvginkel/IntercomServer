namespace IntercomTestWeb;

// Resolves where the JSON persistence files live (D13). Defaults to a "data" directory beside the
// running app (matching IntercomServer's convention); override with the DATA_DIR environment
// variable. Replaces %AppData%\Intercom Test and the HKCU\Webathome\Intercom Test registry prefs.
public sealed class DataPaths
{
    public string DataDir { get; }

    public string DevicesFile => Path.Combine(DataDir, "Devices.json");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    public DataPaths()
    {
        DataDir = Env.Str("DATA_DIR") is { Length: > 0 } dir ? dir : "data";

        Directory.CreateDirectory(DataDir);
    }
}
