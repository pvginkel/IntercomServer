using System.Text.Json;
using Serilog;

namespace IntercomTestWeb.Services;

// Persists server-level preferences to settings.json (D13), replacing the
// HKCU\Webathome\Intercom Test "Auto Accept" registry value.
public sealed class SettingsStore
{
    private static readonly ILogger Logger = Log.ForContext<SettingsStore>();

    private sealed record Settings(bool AutoAccept = false);

    private readonly string _path;
    private readonly Lock _syncRoot = new();
    private Settings _settings;

    public SettingsStore(DataPaths paths)
    {
        _path = paths.SettingsFile;
        _settings = Load(_path);
    }

    public bool AutoAccept
    {
        get
        {
            lock (_syncRoot)
                return _settings.AutoAccept;
        }
        set
        {
            lock (_syncRoot)
            {
                _settings = _settings with { AutoAccept = value };
                Save();
            }
        }
    }

    private static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<Settings>(stream, IntercomJson.Options)
                    ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to read settings from {Path}; using defaults", path);
        }

        return new Settings();
    }

    private void Save()
    {
        try
        {
            using var stream = File.Create(_path);
            JsonSerializer.Serialize(stream, _settings, IntercomJson.Options);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to write settings to {Path}", _path);
        }
    }
}
