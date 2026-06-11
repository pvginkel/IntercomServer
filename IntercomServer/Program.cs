using System.Globalization;
using IntercomServer;
using IntercomServer.AIAssistant;
using IntercomServer.AIAssistant.ChatGpt;
using IntercomServer.AIAssistant.Gemini;
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
        var chatGptConfiguration = new ChatGptConfiguration
        {
            ApiKey = Env("OPENAI_API_KEY"),
            Model = Env("CHATGPT_MODEL") is { Length: > 0 } model ? model : "gpt-realtime-2",
            WebSearchModel = Env("CHATGPT_WEB_SEARCH_MODEL") is { Length: > 0 } searchModel
                ? searchModel
                : "gpt-5.5",
            Voice = Env("CHATGPT_VOICE") is { Length: > 0 } voice ? voice : "marin",
        };
        var geminiConfiguration = new GeminiConfiguration
        {
            ApiKey = Env("GOOGLE_API_KEY"),
            Model = Env("GOOGLE_CHAT_MODEL") is { Length: > 0 } chatModel
                ? chatModel
                : new GeminiConfiguration().Model,
            Voice = Env("GOOGLE_VOICE") is { Length: > 0 } googleVoice
                ? googleVoice
                : new GeminiConfiguration().Voice,
        };
        var assistantConfiguration = new AssistantConfiguration
        {
            Locale = Env("ASSISTANT_LOCALE"),
            Instructions = ResolveFileOrDefault(
                "ASSISTANT_INSTRUCTIONS_FILE",
                new AssistantConfiguration().Instructions
            ),
            // Required when the feature is enabled; AssistantConfiguration.Validate enforces it.
            CloseOutPrompt = ResolveFileOrDefault("ASSISTANT_CLOSE_OUT_PROMPT_FILE", ""),
            CloseOutTimeoutSeconds = int.TryParse(
                Env("ASSISTANT_CLOSE_OUT_TIMEOUT_SECONDS"),
                out var closeOutTimeout
            )
                ? closeOutTimeout
                : new AssistantConfiguration().CloseOutTimeoutSeconds,
            McpConfigFile = Env("MCP_CONFIG_FILE") is { Length: > 0 } mcpFile
                ? mcpFile
                : "mcpservers.json",
            DataDirectory = Env("DATA_DIR") is { Length: > 0 } dataDir ? dataDir : "data",
            DebugAudioDirectory = Env("ASSISTANT_DEBUG_AUDIO_DIR"),
            MicGateThreshold = double.TryParse(
                Env("ASSISTANT_MIC_GATE_THRESHOLD"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var micGate
            )
                ? micGate
                : new AssistantConfiguration().MicGateThreshold,
            MicGateAttackMs = int.TryParse(Env("ASSISTANT_MIC_GATE_ATTACK_MS"), out var micAttack)
                ? micAttack
                : new AssistantConfiguration().MicGateAttackMs,
            MicGateHoldMs = int.TryParse(Env("ASSISTANT_MIC_GATE_HOLD_MS"), out var micHold)
                ? micHold
                : new AssistantConfiguration().MicGateHoldMs,
            MicGatePrerollMs = int.TryParse(
                Env("ASSISTANT_MIC_GATE_PREROLL_MS"),
                out var micPreroll
            )
                ? micPreroll
                : new AssistantConfiguration().MicGatePrerollMs,
        };

        // ASSISTANT_VOICE_PROVIDER selects which AI provider backs the conversations. When it
        // is not set the feature is off; when it names a provider whose API key is missing the
        // host refuses to start rather than misbehaving at the first button press.
        var voiceProvider = Env("ASSISTANT_VOICE_PROVIDER");
        switch (voiceProvider?.ToLowerInvariant())
        {
            case "chatgpt":
                if (!chatGptConfiguration.IsEnabled)
                    throw new InvalidOperationException(
                        "OPENAI_API_KEY is required when ASSISTANT_VOICE_PROVIDER is 'chatgpt'."
                    );
                services.AddSingleton<IAssistantSessionFactory, ChatGptSessionFactory>();
                break;

            case "google":
                if (!geminiConfiguration.IsEnabled)
                    throw new InvalidOperationException(
                        "GOOGLE_API_KEY is required when ASSISTANT_VOICE_PROVIDER is 'google'."
                    );
                services.AddSingleton<IAssistantSessionFactory, GeminiSessionFactory>();
                break;

            case null or "":
                services.AddSingleton<IAssistantSessionFactory, DisabledSessionFactory>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown ASSISTANT_VOICE_PROVIDER '{voiceProvider}'; use 'chatgpt' or 'google'."
                );
        }

        // Fails fast on a fatal misconfiguration before the host starts in a broken state.
        assistantConfiguration.Validate(enabled: !string.IsNullOrEmpty(voiceProvider));

        services.AddSingleton(chatGptConfiguration);
        services.AddSingleton(geminiConfiguration);
        services.AddSingleton(assistantConfiguration);
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
