using System.Globalization;
using IntercomServer;
using IntercomServer.ChatGpt;
using IntercomServer.Utils;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Serilog;
using Serilog.Extensions.Logging;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.Debug()
    .CreateLogger();

await Program
    .CreateHostBuilder(Environment.GetEnvironmentVariable)
    .RunConsoleAsync(options => options.SuppressStatusMessages = true);

internal partial class Program
{
    /// <summary>
    /// The server's composition: every registration, with its settings parsed from
    /// <paramref name="env"/>. Production passes the process environment; the test suite passes
    /// each test's own settings, so every test composes the server the way production does.
    /// </summary>
    internal static IHostBuilder CreateHostBuilder(Func<string, string?> env)
    {
        // Returns the contents of the file named by the given environment variable, or the
        // supplied default when the variable is unset (or the file cannot be read). Used for the
        // prompts that can be overridden by pointing an env var at a text file.
        string ResolveFileOrDefault(string fileEnvVar, string @default)
        {
            var file = env(fileEnvVar);
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

        // Fails fast on a fatal misconfiguration (see ChatGptConfiguration.Validate) before the
        // config is registered, so the host never starts in a broken state.
        static ChatGptConfiguration Validated(ChatGptConfiguration configuration)
        {
            configuration.Validate();
            return configuration;
        }

        var webHostConfiguration = new WebHostConfiguration
        {
            Port = env("HTTP_PORT") is { Length: > 0 } httpPort
                ? int.Parse(httpPort)
                : new WebHostConfiguration().Port,
        };

        var signInConfiguration = new SignInConfiguration
        {
            Issuer = env("KEYCLOAK_ISSUER"),
            Audience = env("KEYCLOAK_AUDIENCE") is { Length: > 0 } audience
                ? audience
                : new SignInConfiguration().Audience,
        };
        signInConfiguration.Validate();

        var dataConfiguration = new DataConfiguration
        {
            Directory = env("DATA_DIR") is { Length: > 0 } dataDir
                ? dataDir
                : new DataConfiguration().Directory,
        };

        var trackConfiguration = new TrackConfiguration
        {
            MaxDuration = env("TRACK_MAX_SECONDS") is { Length: > 0 } maxSeconds
                ? TimeSpan.FromSeconds(int.Parse(maxSeconds, CultureInfo.InvariantCulture))
                : new TrackConfiguration().MaxDuration,
            MaxBytes = env("TRACK_MAX_BYTES") is { Length: > 0 } maxBytes
                ? long.Parse(maxBytes, CultureInfo.InvariantCulture)
                : new TrackConfiguration().MaxBytes,
        };
        trackConfiguration.Validate();

        var builder = new HostBuilder()
            // The framework logs through Serilog too, its warnings and errors, and the address the
            // web host listens on. AddSerilog would add a rule of its own letting every level
            // through, which these filters cannot narrow.
            .ConfigureLogging(logging =>
                logging
                    .SetMinimumLevel(LogLevel.Warning)
                    .AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information)
                    .AddProvider(new SerilogLoggerProvider())
            )
            // ConfigureWebHost registers the web host's hosted service, and hosted services start in
            // registration order: the probes are answered from the start, while Service.StartAsync is
            // still dialing the MQTT broker and discovering MCP servers (which can block). The sounds
            // are decoded, and ffprobe found, before Service connects, so nothing plays before they are.
            .ConfigureWebHost(
                web =>
                    web.UseKestrel(kestrel => kestrel.ListenAnyIP(webHostConfiguration.Port))
                        .ConfigureServices(services => services.AddRouting().AddSignIn(signInConfiguration))
                        .Configure(app =>
                            app.UseRouting()
                                .UseAuthentication()
                                .UseAuthorization()
                                .UseEndpoints(endpoints =>
                                {
                                    endpoints.MapHealthEndpoints();
                                    // Every /api route takes a bearer token; a route asks for its role on top.
                                    var api = endpoints.MapGroup("/api").RequireAuthorization();
                                    endpoints.MapAppReleaseEndpoints(api);
                                    api.MapTrackEndpoints();
                                    api.MapDeviceEndpoints();
                                })
                        ),
                // The aspnet image sets ASPNETCORE_HTTP_PORTS. The web host reads no ASPNETCORE_
                // variable, so HTTP_PORT alone decides its port.
                options => options.SuppressEnvironmentConfiguration = true
            );

        return builder.ConfigureServices(
            (context, services) =>
            {
                services.AddHostedService(p => p.GetRequiredService<SoundLibrary>());
                services.AddHostedService(p => p.GetRequiredService<TrackStore>());
                services.AddHostedService<Service>();
                // Hosted services stop in the reverse of this order, so the playbacks end, and the
                // switch publishes their ends, while the server is still connected.
                services.AddHostedService(p => p.GetRequiredService<AmbientSwitch>());
                services.AddHostedService(p => p.GetRequiredService<AmbientPlayer>());
                services.AddSingleton(
                    new ServerConfiguration
                    {
                        Host = env("MQTT_HOST"),
                        Port = string.IsNullOrEmpty(env("MQTT_PORT")) ? null : int.Parse(env("MQTT_PORT")!),
                        Username = env("MQTT_USERNAME"),
                        Password = env("MQTT_PASSWORD")
                    }
                );
                var devicesConfiguration = new DevicesConfiguration
                {
                    TopicPrefixes = env("DEVICES_TOPIC_PREFIXES") is { Length: > 0 } topicPrefixes
                        ? topicPrefixes.Split(',', StringSplitOptions.TrimEntries)
                        : new DevicesConfiguration().TopicPrefixes,
                };
                devicesConfiguration.Validate();
                services.AddSingleton(devicesConfiguration);
                var ambientConfiguration = new AmbientConfiguration
                {
                    TopicPrefixes = env("AMBIENT_TOPIC_PREFIXES") is { Length: > 0 } ambientPrefixes
                        ? ambientPrefixes.Split(',', StringSplitOptions.TrimEntries)
                        : new AmbientConfiguration().TopicPrefixes,
                    Loudness = env("AMBIENT_LOUDNESS_LUFS") is { Length: > 0 } loudness
                        ? double.Parse(loudness, NumberStyles.Float, CultureInfo.InvariantCulture)
                        : new AmbientConfiguration().Loudness,
                    Fade = env("AMBIENT_FADE_MS") is { Length: > 0 } fadeMs
                        ? TimeSpan.FromMilliseconds(int.Parse(fadeMs, CultureInfo.InvariantCulture))
                        : new AmbientConfiguration().Fade,
                    Prebuffer = env("AMBIENT_PREBUFFER_MS") is { Length: > 0 } prebufferMs
                        ? TimeSpan.FromMilliseconds(int.Parse(prebufferMs, CultureInfo.InvariantCulture))
                        : new AmbientConfiguration().Prebuffer,
                    Cap = env("AMBIENT_CAP_SECONDS") is { Length: > 0 } capSeconds
                        ? TimeSpan.FromSeconds(int.Parse(capSeconds, CultureInfo.InvariantCulture))
                        : new AmbientConfiguration().Cap,
                };
                ambientConfiguration.Validate(devicesConfiguration);
                services.AddSingleton(ambientConfiguration);
                var doorbellConfiguration = new DoorbellConfiguration
                {
                    ExtraDevices = env("DOORBELL_EXTRA_DEVICES") is { Length: > 0 } extraDevices
                        ? extraDevices.Split(',', StringSplitOptions.TrimEntries)
                        : new DoorbellConfiguration().ExtraDevices,
                };
                doorbellConfiguration.Validate();
                services.AddSingleton(doorbellConfiguration);
                services.AddSingleton(
                    Validated(new ChatGptConfiguration
                    {
                        ApiKey = env("OPENAI_API_KEY"),
                        Model = env("CHATGPT_MODEL") is { Length: > 0 } model ? model : "gpt-realtime-2",
                        WebSearchModel = env("CHATGPT_WEB_SEARCH_MODEL") is { Length: > 0 } searchModel
                            ? searchModel
                            : "gpt-5.5",
                        Voice = env("CHATGPT_VOICE") is { Length: > 0 } voice ? voice : "marin",
                        Locale = env("CHATGPT_LOCALE"),
                        Instructions = ResolveFileOrDefault(
                            "CHATGPT_INSTRUCTIONS_FILE",
                            new ChatGptConfiguration().Instructions
                        ),
                        // Required when the feature is enabled; ChatGptConfiguration.Validate enforces it.
                        CloseOutPrompt = ResolveFileOrDefault("CHATGPT_CLOSE_OUT_PROMPT_FILE", ""),
                        CloseOutTimeoutSeconds = env("CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS") is { Length: > 0 } closeOutTimeout
                            ? int.Parse(closeOutTimeout, CultureInfo.InvariantCulture)
                            : new ChatGptConfiguration().CloseOutTimeoutSeconds,
                        McpConfigFile = env("MCP_CONFIG_FILE") is { Length: > 0 } mcpFile
                            ? mcpFile
                            : "mcpservers.json",
                        DataDirectory = dataConfiguration.Directory,
                        DebugAudioDirectory = env("CHATGPT_DEBUG_AUDIO_DIR"),
                        MicGateThreshold = env("CHATGPT_MIC_GATE_THRESHOLD") is { Length: > 0 } micGate
                            ? double.Parse(micGate, NumberStyles.Float, CultureInfo.InvariantCulture)
                            : new ChatGptConfiguration().MicGateThreshold,
                        MicGateAttackMs = env("CHATGPT_MIC_GATE_ATTACK_MS") is { Length: > 0 } micAttack
                            ? int.Parse(micAttack, CultureInfo.InvariantCulture)
                            : new ChatGptConfiguration().MicGateAttackMs,
                        MicGateHoldMs = env("CHATGPT_MIC_GATE_HOLD_MS") is { Length: > 0 } micHold
                            ? int.Parse(micHold, CultureInfo.InvariantCulture)
                            : new ChatGptConfiguration().MicGateHoldMs,
                        MicGatePrerollMs = env("CHATGPT_MIC_GATE_PREROLL_MS") is { Length: > 0 } micPreroll
                            ? int.Parse(micPreroll, CultureInfo.InvariantCulture)
                            : new ChatGptConfiguration().MicGatePrerollMs,
                    })
                );
                services.AddSingleton(
                    new AudioServerConfiguration
                    {
                        Port = string.IsNullOrEmpty(env("AUDIO_PORT"))
                            ? 5004
                            : int.Parse(env("AUDIO_PORT")!),
                        Host = env("AUDIO_HOST"),
                    }
                );
                services.AddSingleton(dataConfiguration);
                services.AddSingleton<AppReleaseStore>();
                services.AddSingleton(trackConfiguration);
                services.AddSingleton(new AudioProbe("ffprobe"));
                services.AddSingleton<TrackStore>();
                services.AddSingleton<Server>();
                services.AddSingleton<DeviceManager>();
                services.AddSingleton<StateManager>();
                services.AddSingleton<MqttClientFactory>();
                services.AddSingleton<AlarmManager>();
                services.AddSingleton<PlaybackManager>();
                services.AddSingleton<AmbientPlayer>();
                services.AddSingleton<AmbientSwitch>();
                services.AddSingleton(new AudioDecoder("ffmpeg"));
                services.AddSingleton<SoundLibrary>();
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
    }
}
