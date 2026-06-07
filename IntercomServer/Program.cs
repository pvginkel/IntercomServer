using IntercomServer;
using IntercomServer.ChatGpt;
using IntercomServer.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Serilog;

static string? Env(string name) => Environment.GetEnvironmentVariable(name);

// Resolves the ChatGPT system prompt: a file (CHATGPT_INSTRUCTIONS_FILE) takes precedence,
// then an inline value (CHATGPT_INSTRUCTIONS), otherwise the built-in default.
static string ResolveInstructions()
{
    var file = Env("CHATGPT_INSTRUCTIONS_FILE");
    if (!string.IsNullOrEmpty(file))
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Could not read CHATGPT_INSTRUCTIONS_FILE '{File}'; falling back to CHATGPT_INSTRUCTIONS / default.",
                file
            );
        }
    }

    return Env("CHATGPT_INSTRUCTIONS") is { Length: > 0 } instructions
        ? instructions
        : new ChatGptConfiguration().Instructions;
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
                Model = Env("CHATGPT_MODEL") is { Length: > 0 } model ? model : "gpt-realtime",
                Voice = Env("CHATGPT_VOICE") is { Length: > 0 } voice ? voice : "marin",
                Instructions = ResolveInstructions(),
                McpConfigFile = Env("MCP_CONFIG_FILE") is { Length: > 0 } mcpFile
                    ? mcpFile
                    : "mcpservers.json",
                DebugAudioDirectory = Env("CHATGPT_DEBUG_AUDIO_DIR"),
            }
        );
        services.AddSingleton(
            new AudioServerConfiguration
            {
                Port = string.IsNullOrEmpty(Env("AUDIO_PORT")) ? 5004 : int.Parse(Env("AUDIO_PORT")!),
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
        services.AddSingleton<ConversationManager>();
        services.AddSingleton(p => p.GetRequiredService<MqttClientFactory>().CreateMqttClient());
    }
);

await builder.RunConsoleAsync();
