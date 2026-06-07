// The OpenAI .NET Realtime API is shipped as an experimental (evaluation) surface and
// raises OPENAI002. We knowingly depend on it; suppress the diagnostic for this file.
#pragma warning disable OPENAI002

using System.Net;
using IntercomServer.ChatGpt.Audio;
using IntercomServer.Utils;
using NAudio.Wave;
using OpenAI.Realtime;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// A single live ChatGPT voice conversation with one device. It bridges audio between
/// the device and the OpenAI Realtime API; it does not control the device (LEDs,
/// recording, endpoints) — that is <see cref="StateManager"/>'s responsibility.
///
/// The device's microphone arrives over UDP on the shared <see cref="UdpAudioServer"/>;
/// this conversation filters by the device's source endpoint, resamples 16 kHz -> 24 kHz
/// and forwards it to OpenAI. The model's audio is resampled 24 kHz -> 16 kHz and sent
/// back to the device over UDP via <see cref="AudioSender"/>.
/// </summary>
internal sealed class Conversation
{
    private const string EndConversationTool = "end_conversation";
    private const int OpenAiSampleRate = 24000;

    private static readonly ILogger Logger = Log.ForContext<Conversation>();

    private readonly ChatGptConfiguration _configuration;
    private readonly McpToolRegistry _mcp;
    private readonly AudioSender _audioSender;
    private readonly UdpAudioServer _audioServer;
    private readonly Action<Conversation> _onEnded;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly IPEndPoint _deviceEndpoint;
    private readonly StreamingResampler _micResampler =
        new(Constants.AudioFormat.SampleRate, OpenAiSampleRate);
    private readonly StreamingResampler _outResampler =
        new(OpenAiSampleRate, Constants.AudioFormat.SampleRate);
    private readonly LiveAudioStream _audioOut = new();

    private readonly object _writerLock = new();
    private WaveFileWriter? _receivedWriter;
    private WaveFileWriter? _sentWriter;

    private RealtimeSessionClient? _session;
    private int _ending;

    public Device Device { get; }

    public Conversation(
        Device device,
        ChatGptConfiguration configuration,
        McpToolRegistry mcp,
        AudioSender audioSender,
        UdpAudioServer audioServer,
        Action<Conversation> onEnded
    )
    {
        Device = device;
        _configuration = configuration;
        _mcp = mcp;
        _audioSender = audioSender;
        _audioServer = audioServer;
        _onEnded = onEnded;
        _deviceEndpoint = IPEndPoint.Parse(device.Configuration!.Endpoint!);
    }

    public async Task StartAsync()
    {
        var realtimeClient = new RealtimeClient(_configuration.ApiKey!);
        var session = await realtimeClient.StartConversationSessionAsync(
            _configuration.Model,
            cancellationToken: _cts.Token
        );
        _session = session;

        CreateDebugWriters();

        await SendGuardedAsync(
            () => session.ConfigureConversationSessionAsync(BuildSessionOptions(), _cts.Token)
        );

        // Greet first so the device user hears something immediately.
        await SendGuardedAsync(() => session.StartResponseAsync(_cts.Token));

        _audioServer.Data += OnAudioReceived;

        // Pace the model's audio out to the device in real time. The device's jitter buffer
        // is small and is overrun (garbled) if we forward OpenAI's faster-than-real-time
        // stream as-is; AudioStreaming is the same pacing the ring/doorbell playback uses.
        _ = RunAudioPump();

        // Only start consuming updates once setup succeeded, so a failed start never
        // raises the "ended" callback.
        _ = ReceiveLoop(session, _cts.Token);
    }

    /// <summary>Tears down the conversation without notifying (used for a failed start).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ending, 1) == 1)
            return;

        Cleanup();
    }

    /// <summary>Ends the conversation (idempotent) and notifies via the ended callback.</summary>
    public void End()
    {
        if (Interlocked.Exchange(ref _ending, 1) == 1)
            return;

        Cleanup();
        _onEnded(this);
    }

    private void Cleanup()
    {
        _audioServer.Data -= OnAudioReceived;
        _audioOut.Complete();

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _session?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _cts.Dispose();

        lock (_writerLock)
        {
            try
            {
                _receivedWriter?.Dispose();
                _sentWriter?.Dispose();
            }
            catch
            {
                // Ignore.
            }
            finally
            {
                _receivedWriter = null;
                _sentWriter = null;
            }
        }
    }

    private void CreateDebugWriters()
    {
        var directory = _configuration.DebugAudioDirectory;
        if (string.IsNullOrEmpty(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var deviceId = string.Concat(
                Device.DeviceId.Select(c => char.IsLetterOrDigit(c) ? c : '_')
            );

            _receivedWriter = new WaveFileWriter(
                Path.Combine(directory, $"{stamp}_{deviceId}_received_{OpenAiSampleRate}.wav"),
                new WaveFormat(OpenAiSampleRate, 16, 1)
            );
            _sentWriter = new WaveFileWriter(
                Path.Combine(
                    directory,
                    $"{stamp}_{deviceId}_sent_{Constants.AudioFormat.SampleRate}.wav"
                ),
                new WaveFormat(Constants.AudioFormat.SampleRate, 16, 1)
            );

            Logger.Information(
                "Recording ChatGPT audio for device {Device} to {Directory}",
                Device.DeviceId,
                directory
            );
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start ChatGPT debug audio recording");
        }
    }

    private async Task ReceiveLoop(
        RealtimeSessionClient session,
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
                        var received = audio.Delta.ToArray();
                        var pcm16 = _outResampler.Resample(received);

                        // Debug capture: the raw audio received from OpenAI and the
                        // resampled audio we send to the device.
                        lock (_writerLock)
                        {
                            _receivedWriter?.Write(received, 0, received.Length);
                            if (pcm16.Length > 0)
                                _sentWriter?.Write(pcm16, 0, pcm16.Length);
                        }

                        if (pcm16.Length > 0)
                            _audioOut.Append(pcm16);
                        break;

                    case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall:
                        await HandleFunctionCall(session, functionCall, cancellationToken);
                        break;

                    case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                        // The user started speaking. Server VAD (interrupt_response) cancels
                        // any in-flight model response; drop the audio we have buffered so the
                        // assistant stops promptly instead of talking over the user.
                        _audioOut.Discard();
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
            // Conversation is ending.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ChatGPT receive loop failed for device {Device}", Device.DeviceId);
        }
        finally
        {
            // The socket closed (server hang-up, network error, or our own teardown);
            // make sure the device is reset. End is idempotent.
            End();
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
            Logger.Information(
                "Model requested to end the conversation with {Device}.",
                Device.DeviceId
            );
            await SendGuardedAsync(
                () =>
                    session.AddItemAsync(
                        new RealtimeFunctionCallOutputItem(functionCall.CallId, "Goodbye."),
                        cancellationToken
                    )
            );
            End();
            return;
        }

        var arguments = functionCall.FunctionArguments?.ToString() ?? "{}";
        Logger.Information("Model called tool {Tool} with {Arguments}", name, arguments);

        string output;
        try
        {
            output = await _mcp.CallAsync(name, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MCP tool {Tool} failed", name);
            output = $"The tool failed: {ex.Message}";
        }

        await SendGuardedAsync(
            () =>
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
        if (session == null || _ending == 1)
            return;

        // Identify this device's microphone stream by its source endpoint, and strip the
        // 4-byte sequence header (see AudioSender) before resampling.
        if (!e.RemoteEndpoint.Equals(_deviceEndpoint) || e.Data.Length <= 4)
            return;

        try
        {
            var pcm24 = _micResampler.Resample(e.Data.AsSpan(4));
            if (pcm24.Length == 0)
                return;

            await SendGuardedAsync(
                () => session.SendInputAudioAsync(BinaryData.FromBytes(pcm24), _cts.Token)
            );
        }
        catch (OperationCanceledException)
        {
            // Conversation ending.
        }
        catch (ObjectDisposedException)
        {
            // Conversation ending.
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to forward microphone audio to ChatGPT");
        }
    }

    private async Task RunAudioPump()
    {
        try
        {
            await AudioStreaming.PlayAsync(
                _audioSender,
                [_deviceEndpoint],
                _audioOut,
                configuration: null,
                _cts.Token
            );
        }
        catch (OperationCanceledException)
        {
            // Conversation is ending.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ChatGPT audio pump failed for device {Device}", Device.DeviceId);
        }
    }

    private RealtimeConversationSessionOptions BuildSessionOptions()
    {
        var options = new RealtimeConversationSessionOptions
        {
            Instructions = _configuration.Instructions,
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
                    Voice = _configuration.Voice,
                },
            },
        };

        options.OutputModalities.Add(RealtimeOutputModality.Audio);

        foreach (var tool in _mcp.GetRealtimeTools())
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
}
