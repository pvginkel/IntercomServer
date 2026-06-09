using System.Globalization;
using IntercomServer;
using IntercomServer.ChatGpt;
using IntercomServer.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Serilog;

static string? Env(string name) => Environment.GetEnvironmentVariable(name);

// Returns the contents of the file named by the given environment variable, or the supplied
// default when the variable is unset (or the file cannot be read). Used for the prompts that
// can be overridden by pointing an env var at a text file.
static string ResolveFileOrDefault(string fileEnvVar, string @default)
{
    var file = Env(fileEnvVar);
    if (string.IsNullOrEmpty(file))
        return @default;

    try
    {
        return File.ReadAllText(file);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not read {EnvVar} '{File}'; falling back to the default.", fileEnvVar, file);
        return @default;
    }
}

// Fails fast on a fatal misconfiguration (see ChatGptConfiguration.Validate) before the config is
// registered, so the host never starts in a broken state.
static ChatGptConfiguration Validated(ChatGptConfiguration configuration)
{
    configuration.Validate();
    return configuration;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.Debug()
    .CreateLogger();

var builder = new HostBuilder().ConfigureServices(
    (context, services) =>
    {
        services.AddHostedService<Service>();
        services.AddSingleton(
            new ServerConfiguration
            {
                Host = Env("MQTT_HOST"),
                Port = string.IsNullOrEmpty(Env("MQTT_PORT")) ? null : int.Parse(Env("MQTT_PORT")!),
                Username = Env("MQTT_USERNAME"),
                Password = Env("MQTT_PASSWORD")
            }
        );
        services.AddSingleton(
            Validated(new ChatGptConfiguration
            {
                ApiKey = Env("OPENAI_API_KEY"),
                Model = Env("CHATGPT_MODEL") is { Length: > 0 } model ? model : "gpt-realtime-2",
                WebSearchModel = Env("CHATGPT_WEB_SEARCH_MODEL") is { Length: > 0 } searchModel
                    ? searchModel
                    : "gpt-5.5",
                Voice = Env("CHATGPT_VOICE") is { Length: > 0 } voice ? voice : "marin",
                Locale = Env("CHATGPT_LOCALE"),
                Instructions = ResolveFileOrDefault(
                    "CHATGPT_INSTRUCTIONS_FILE",
                    new ChatGptConfiguration().Instructions
                ),
                // Required when the feature is enabled; ChatGptConfiguration.Validate enforces it.
                CloseOutPrompt = ResolveFileOrDefault("CHATGPT_CLOSE_OUT_PROMPT_FILE", ""),
                CloseOutTimeoutSeconds = int.TryParse(
                    Env("CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS"),
                    out var closeOutTimeout
                )
                    ? closeOutTimeout
                    : new ChatGptConfiguration().CloseOutTimeoutSeconds,
                McpConfigFile = Env("MCP_CONFIG_FILE") is { Length: > 0 } mcpFile
                    ? mcpFile
                    : "mcpservers.json",
                DataDirectory = Env("DATA_DIR") is { Length: > 0 } dataDir ? dataDir : "data",
                DebugAudioDirectory = Env("CHATGPT_DEBUG_AUDIO_DIR"),
                MicGateThreshold = double.TryParse(
                    Env("CHATGPT_MIC_GATE_THRESHOLD"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var micGate
                )
                    ? micGate
                    : new ChatGptConfiguration().MicGateThreshold,
                MicGateAttackMs = int.TryParse(Env("CHATGPT_MIC_GATE_ATTACK_MS"), out var micAttack)
                    ? micAttack
                    : new ChatGptConfiguration().MicGateAttackMs,
                MicGateHoldMs = int.TryParse(Env("CHATGPT_MIC_GATE_HOLD_MS"), out var micHold)
                    ? micHold
                    : new ChatGptConfiguration().MicGateHoldMs,
                MicGatePrerollMs = int.TryParse(
                    Env("CHATGPT_MIC_GATE_PREROLL_MS"),
                    out var micPreroll
                )
                    ? micPreroll
                    : new ChatGptConfiguration().MicGatePrerollMs,
            })
        );
        services.AddSingleton(
            new AudioServerConfiguration
            {
                Port = string.IsNullOrEmpty(Env("AUDIO_PORT"))
                    ? 5004
                    : int.Parse(Env("AUDIO_PORT")!),
                Host = Env("AUDIO_HOST"),
            }
        );
        services.AddSingleton<Server>();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<StateManager>();
        services.AddSingleton<MqttClientFactory>();
        services.AddSingleton<AlarmManager>();
        services.AddSingleton<PlaybackManager>();
        services.AddSingleton<AudioSender>();
        services.AddSingleton(p => new UdpAudioServer(
            p.GetRequiredService<AudioServerConfiguration>().Port
        ));
        services.AddSingleton<AudioEndpointResolver>();
        services.AddSingleton<McpToolRegistry>();
        services.AddSingleton<WebSearchTool>();
        services.AddSingleton<MemoryStore>();
        services.AddSingleton<ConversationCloser>();
        services.AddSingleton<ConversationManager>();
        services.AddSingleton(p => p.GetRequiredService<MqttClientFactory>().CreateMqttClient());
    }
);

await builder.RunConsoleAsync();
