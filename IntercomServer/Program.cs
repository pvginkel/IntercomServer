using IntercomServer;
using IntercomServer.ChatGpt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Serilog;

static string? Env(string name) => Environment.GetEnvironmentVariable(name);

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
                Host = Environment.GetEnvironmentVariable("MQTT_HOST"),
                Port = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MQTT_PORT"))
                    ? null
                    : int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT")!),
                Username = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
                Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD")
            }
        );
        services.AddSingleton(
            new ChatGptConfiguration
            {
                ApiKey = Env("OPENAI_API_KEY"),
                Model = Env("CHATGPT_MODEL") is { Length: > 0 } model ? model : "gpt-realtime",
                Voice = Env("CHATGPT_VOICE") is { Length: > 0 } voice ? voice : "marin",
                Instructions = Env("CHATGPT_INSTRUCTIONS") is { Length: > 0 } instructions
                    ? instructions
                    : new ChatGptConfiguration().Instructions,
                AudioListenPort = string.IsNullOrEmpty(Env("CHATGPT_AUDIO_PORT"))
                    ? 5004
                    : int.Parse(Env("CHATGPT_AUDIO_PORT")!),
                AdvertisedHost = Env("CHATGPT_AUDIO_HOST"),
                McpConfigFile = Env("MCP_CONFIG_FILE") is { Length: > 0 } mcpFile
                    ? mcpFile
                    : "mcpservers.json",
            }
        );
        services.AddSingleton<Server>();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<StateManager>();
        services.AddSingleton<MqttClientFactory>();
        services.AddSingleton<AlarmManager>();
        services.AddSingleton<PlaybackManager>();
        services.AddSingleton<AudioSender>();
        services.AddSingleton<McpToolRegistry>();
        services.AddSingleton<ConversationManager>();
        services.AddSingleton(p => p.GetRequiredService<MqttClientFactory>().CreateMqttClient());
    }
);

await builder.RunConsoleAsync();
