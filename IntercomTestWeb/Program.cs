using System.Text.Json;
using System.Text.Json.Serialization;
using IntercomTestWeb;
using IntercomTestWeb.Endpoints;
using IntercomTestWeb.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.Debug()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Bind 0.0.0.0 so the LAN hostname works (§12); override the port with HTTP_PORT.
    builder.WebHost.UseUrls($"http://0.0.0.0:{Env.Int("HTTP_PORT", 8081)}");

    // Use the centralized snake_case / ignore-null options for the REST API too, so request and
    // response bodies share the device wire shape.
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    var services = builder.Services;

    services.AddSingleton(
        new ServerConfiguration
        {
            Host = Env.Str("MQTT_HOST"),
            Port = Env.IntOrNull("MQTT_PORT"),
            Username = Env.Str("MQTT_USERNAME"),
            Password = Env.Str("MQTT_PASSWORD"),
        }
    );

    services.AddSingleton<DataPaths>();
    services.AddSingleton<SettingsStore>();
    services.AddSingleton<EventHub>();
    services.AddSingleton<DeviceRegistry>();
    services.AddSingleton<SimDeviceManager>();
    services.AddSingleton<MqttControlService>();

    // SimDeviceManager starts first so every simulated-device id is registered before the control
    // client subscribes and would otherwise treat a simulated device's retained configuration as a
    // real device.
    services.AddHostedService(sp => sp.GetRequiredService<SimDeviceManager>());
    services.AddHostedService(sp => sp.GetRequiredService<MqttControlService>());

    var app = builder.Build();

    app.UseWebSockets();
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapApiEndpoints();
    app.MapEventsWebSocket();
    app.MapAudioWebSocket();

    // SPA fallback for any non-API route.
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
