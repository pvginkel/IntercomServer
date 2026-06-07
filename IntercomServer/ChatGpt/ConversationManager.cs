// The OpenAI .NET Realtime API is shipped as an experimental (evaluation) surface and
// raises OPENAI002. We knowingly depend on it; suppress the diagnostic for this file.
#pragma warning disable OPENAI002

using System.Net;
using IntercomServer.ChatGpt.Audio;
using IntercomServer.Utils;
using MQTTnet;
using OpenAI.Realtime;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// Orchestrates a single, exclusive ChatGPT voice conversation with one device.
///
/// Audio flow: the device streams its microphone over UDP to <see cref="AudioReceiver"/>
/// (the server's advertised audio endpoint). That PCM is resampled 16 kHz -> 24 kHz and
/// pushed to the OpenAI Realtime API. The model's audio is resampled 24 kHz -> 16 kHz and
/// sent back to the device over UDP via <see cref="AudioSender"/> (the same path the
/// server already uses to play ring tones to a device).
///
/// MCP tools are exposed to the model as ordinary function tools and executed locally by
/// <see cref="McpToolRegistry"/>; nothing is published to the public internet.
/// </summary>
internal sealed class ConversationManager(
    ChatGptConfiguration configuration,
    McpToolRegistry mcp,
    AudioSender audioSender,
    UdpAudioServer audioServer,
    IMqttClient client
)
{
    private const string EndConversationTool = "end_conversation";

    private static readonly ILogger Logger = Log.ForContext<ConversationManager>();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Device? _device;
    private IPEndPoint? _deviceEndpoint;
    private string? _advertisedEndpoint;
    private RealtimeSessionClient? _session;
    private CancellationTokenSource? _cts;
    private StreamingResampler? _micResampler;
    private StreamingResampler? _outResampler;
    private int _ending;

    /// <summary>Raised after a session has been fully torn down (from any cause).</summary>
    public event EventHandler<Device>? SessionEnded;

    /// <summary>
    /// Starts a conversation with <paramref name="device"/>. Returns false when the
    /// feature is not configured or the device cannot be used, in which case no session
    /// is established.
    /// </summary>
    public async Task<bool> StartAsync(Device device)
    {
        if (!configuration.IsEnabled)
        {
            Logger.Warning("ChatGPT conversation requested but no OpenAI API key is configured.");
            return false;
        }

        if (device.Configuration?.Endpoint == null)
        {
            Logger.Warning(
                "Cannot start ChatGPT conversation: device {Device} has no audio endpoint.",
                device.DeviceId
            );
            return false;
        }

        lock (_gate)
        {
            if (_session != null)
            {
                Logger.Warning("A ChatGPT conversation is already active; ignoring request.");
                return false;
            }

            _ending = 0;
            _device = device;
            _deviceEndpoint = IPEndPoint.Parse(device.Configuration.Endpoint);
            _micResampler = new StreamingResampler(Constants.AudioFormat.SampleRate, OpenAiSampleRate);
            _outResampler = new StreamingResampler(OpenAiSampleRate, Constants.AudioFormat.SampleRate);
            _cts = new CancellationTokenSource();
        }

        try
        {
            _advertisedEndpoint = $"{ResolveAdvertisedHost()}:{audioServer.LocalEndPoint.Port}";

            Logger.Information(
                "Starting ChatGPT conversation with device {Device}; mic stream -> {Endpoint}",
                device.DeviceId,
                _advertisedEndpoint
            );

            var realtimeClient = new RealtimeClient(configuration.ApiKey!);
            var session = await realtimeClient.StartConversationSessionAsync(
                configuration.Model,
                cancellationToken: _cts!.Token
            );
            _session = session;

            // Consume server updates on a background loop.
            _ = ReceiveLoop(session, _deviceEndpoint!, _cts.Token);

            await SendGuardedAsync(() =>
                session.ConfigureConversationSessionAsync(BuildSessionOptions(), _cts.Token)
            );

            // Greet first so the device user hears something immediately.
            await SendGuardedAsync(() => session.StartResponseAsync(_cts.Token));

            audioServer.Data += OnAudioReceived;

            await device.AddEndpoint(client, _advertisedEndpoint);
            await device.SetGreenLed(client, Constants.LedOn);
            await device.SetRecording(client, true);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start ChatGPT conversation");
            await EndAsync();
            return false;
        }
    }

    /// <summary>Ends the active conversation (idempotent, safe to call from any thread).</summary>
    public async Task EndAsync()
    {
        if (Interlocked.Exchange(ref _ending, 1) == 1)
            return;

        Device? device;
        string? advertisedEndpoint;
        RealtimeSessionClient? session;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            device = _device;
            advertisedEndpoint = _advertisedEndpoint;
            session = _session;
            cts = _cts;

            _device = null;
            _deviceEndpoint = null;
            _advertisedEndpoint = null;
            _session = null;
            _cts = null;
            _micResampler = null;
            _outResampler = null;
        }

        audioServer.Data -= OnAudioReceived;

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            session?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        if (device != null)
        {
            Logger.Information("Ended ChatGPT conversation with device {Device}", device.DeviceId);

            try
            {
                if (advertisedEndpoint != null)
                    await device.RemoveEndpoint(client, advertisedEndpoint);
                await device.SetRecording(client, false);
                await device.SetGreenLed(client, Constants.LedOff);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to reset device {Device} after ChatGPT", device.DeviceId);
            }

            cts?.Dispose();

            SessionEnded?.Invoke(this, device);
        }
        else
        {
            cts?.Dispose();
        }
    }

    private async Task ReceiveLoop(
        RealtimeSessionClient session,
        IPEndPoint deviceEndpoint,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (var update in session.ReceiveUpdatesAsync(cancellationToken))
            {
                switch (update)
                {
                    case RealtimeServerUpdateResponseOutputAudioDelta audio:
                        var pcm16 = _outResampler?.Resample(audio.Delta.ToArray());
                        if (pcm16 is { Length: > 0 })
                            audioSender.Send(deviceEndpoint, pcm16);
                        break;

                    case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall:
                        await HandleFunctionCall(session, functionCall, cancellationToken);
                        break;

                    case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                        // The user started speaking. Server VAD (interrupt_response) cancels
                        // any in-flight model response automatically.
                        break;

                    case RealtimeServerUpdateError error:
                        Logger.Error(
                            "OpenAI realtime error: {Code} {Message}",
                            error.Error?.Code,
                            error.Error?.Message
                        );
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session is ending.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ChatGPT receive loop failed");
        }
        finally
        {
            // The socket closed (server hang-up, network error, or our own teardown);
            // make sure the device is reset. EndAsync is idempotent.
            await EndAsync();
        }
    }

    private async Task HandleFunctionCall(
        RealtimeSessionClient session,
        RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall,
        CancellationToken cancellationToken
    )
    {
        var name = functionCall.FunctionName;

        if (name == EndConversationTool)
        {
            Logger.Information("Model requested to end the conversation.");
            await SendGuardedAsync(() =>
                session.AddItemAsync(
                    new RealtimeFunctionCallOutputItem(functionCall.CallId, "Goodbye."),
                    cancellationToken
                )
            );
            _ = EndAsync();
            return;
        }

        var arguments = functionCall.FunctionArguments?.ToString() ?? "{}";
        Logger.Information("Model called tool {Tool} with {Arguments}", name, arguments);

        string output;
        try
        {
            output = await mcp.CallAsync(name, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MCP tool {Tool} failed", name);
            output = $"The tool failed: {ex.Message}";
        }

        await SendGuardedAsync(() =>
            session.AddItemAsync(
                new RealtimeFunctionCallOutputItem(functionCall.CallId, output),
                cancellationToken
            )
        );
        await SendGuardedAsync(() => session.StartResponseAsync(cancellationToken));
    }

    private async void OnAudioReceived(object? sender, UdpAudioDataEventArgs e)
    {
        var session = _session;
        var resampler = _micResampler;
        var cts = _cts;
        var deviceEndpoint = _deviceEndpoint;

        if (
            session == null
            || resampler == null
            || cts == null
            || deviceEndpoint == null
            || _ending == 1
        )
            return;

        // Identify the chatting device's microphone stream by its source endpoint.
        if (!e.RemoteEndpoint.Equals(deviceEndpoint) || e.Data.Length <= 4)
            return;

        try
        {
            // Strip the 4-byte sequence header (see AudioSender) before resampling.
            var pcm24 = resampler.Resample(e.Data.AsSpan(4));
            if (pcm24.Length == 0)
                return;

            await SendGuardedAsync(() =>
                session.SendInputAudioAsync(BinaryData.FromBytes(pcm24), cts.Token)
            );
        }
        catch (OperationCanceledException)
        {
            // Session ending.
        }
        catch (ObjectDisposedException)
        {
            // Session ending.
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to forward microphone audio to ChatGPT");
        }
    }

    private RealtimeConversationSessionOptions BuildSessionOptions()
    {
        var options = new RealtimeConversationSessionOptions
        {
            Instructions = configuration.Instructions,
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = new RealtimeConversationSessionInputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    TurnDetection = new RealtimeServerVadTurnDetection
                    {
                        CreateResponseEnabled = true,
                        InterruptResponseEnabled = true,
                    },
                },
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    Voice = configuration.Voice,
                },
            },
        };

        options.OutputModalities.Add(RealtimeOutputModality.Audio);

        foreach (var tool in mcp.GetRealtimeTools())
            options.Tools.Add(tool);

        options.Tools.Add(BuildEndConversationTool());

        return options;
    }

    private static RealtimeFunctionTool BuildEndConversationTool() =>
        new(EndConversationTool)
        {
            FunctionDescription =
                "End the conversation and hang up. Call this when the user says goodbye "
                + "or no longer needs assistance.",
            FunctionParameters = BinaryData.FromString(
                """{"type":"object","properties":{},"additionalProperties":false}"""
            ),
        };

    private string ResolveAdvertisedHost()
    {
        if (!string.IsNullOrEmpty(configuration.AdvertisedHost))
            return configuration.AdvertisedHost;

        var address = NetworkUtils.GetNetworkIPAddresses().FirstOrDefault();
        if (address == null)
        {
            throw new InvalidOperationException(
                "Could not auto-detect a LAN IP address for the audio endpoint. "
                    + "Set the CHATGPT_AUDIO_HOST environment variable."
            );
        }

        return address.ToString();
    }

    private async Task SendGuardedAsync(Func<Task> action)
    {
        await _sendLock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private const int OpenAiSampleRate = 24000;
}
