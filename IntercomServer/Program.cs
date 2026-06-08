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
            new ChatGptConfiguration
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
                MemoryFlushPrompt = ResolveFileOrDefault(
                    "CHATGPT_MEMORY_PROMPT_FILE",
                    new ChatGptConfiguration().MemoryFlushPrompt
                ),
                McpConfigFile = Env("MCP_CONFIG_FILE") is { Length: > 0 } mcpFile
                    ? mcpFile
                    : "mcpservers.json",
                DataDirectory = Env("DATA_DIR") is { Length: > 0 } dataDir ? dataDir : "data",
                DebugAudioDirectory = Env("CHATGPT_DEBUG_AUDIO_DIR"),
            }
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
