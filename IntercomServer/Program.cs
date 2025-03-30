using IntercomServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Serilog;

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
        services.AddSingleton<Server>();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<StateManager>();
        services.AddSingleton<MqttClientFactory>();
        services.AddSingleton<AlarmManager>();
        services.AddSingleton<PlaybackManager>();
        services.AddSingleton<AudioSender>();
        services.AddSingleton(p => p.GetRequiredService<MqttClientFactory>().CreateMqttClient());
    }
);

await builder.RunConsoleAsync();
