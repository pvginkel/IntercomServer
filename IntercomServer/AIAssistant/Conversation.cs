using System.Globalization;
using System.Net;
using IntercomServer.AIAssistant.Audio;
using IntercomServer.Utils;
using NAudio.Wave;
using Serilog;

namespace IntercomServer.AIAssistant;

/// <summary>
/// A single live voice conversation between one device and an AI assistant. It bridges audio
/// between the device and the provider session (<see cref="IAssistantSession"/>) and executes
/// the shared function tools (MCP, memory, end_conversation); it does not control the device
/// (LEDs, recording, endpoints) — that is <see cref="StateManager"/>'s responsibility — and it
/// does not speak the provider's protocol — that is the session's.
///
/// The device's microphone arrives over UDP on the shared <see cref="UdpAudioServer"/>;
/// this conversation filters by the device's source endpoint, resamples 16 kHz to the
/// session's input rate and forwards it. The assistant's audio is resampled from the
/// session's output rate back to 16 kHz and sent to the device over UDP via
/// <see cref="AudioSender"/>.
/// </summary>
internal sealed class Conversation
{
    private const string EndConversationTool = "end_conversation";

    // Buffer this much of the assistant's audio before playback starts, to ride out bursty
    // delivery without starving the device's jitter buffer at the start of a reply.
    private const double PrerollSeconds = 0.15;

    // How much continuous quiet the mic must have seen for a UserSpeechEndedUpdate to close the
    // gate. The signal may be inferred (Gemini: the model starting to speak) and can be wrong —
    // if the mic is loud at that moment the user is most likely still talking, so the signal is
    // dropped and the gate stays open (the provider's barge-in handling sorts out the overlap).
    // Genuine end-of-turn signals comfortably clear this: every provider waits out a silence
    // window of several hundred ms before deciding the user's turn ended.
    private const int SpeechEndedMinQuietMs = 200;

    private static readonly ILogger Logger = Log.ForContext<Conversation>();

    private readonly AssistantConfiguration _configuration;
    private readonly IAssistantSession _session;
    private readonly McpLease _mcpLease;
    private readonly MemoryStore _memory;
    private readonly AudioSender _audioSender;
    private readonly UdpAudioServer _audioServer;
    private readonly Action<Conversation> _onClosing;

    private readonly CancellationTokenSource _cts = new();
    private readonly IPEndPoint _deviceEndpoint;
    private readonly StreamingResampler _micResampler;
    private readonly StreamingResampler _outResampler;
    private readonly LiveAudioStream _audioOut =
        new((int)(Constants.AudioFormat.BytesPerSecond * PrerollSeconds));

    private readonly object _writerLock = new();
    private WaveFileWriter? _receivedWriter;
    private WaveFileWriter? _sentWriter;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _modelInputWriter;

    // Experimental mic noise gate state (see GateMicAudio). Touched only from OnAudioReceived,
    // which the shared UdpAudioServer raises sequentially, so no locking is needed.
    private bool _gateOpen;
    private long _consecutiveLoudSamples; // continuous loud audio, for the attack requirement
    private long _samplesSinceLoud; // quiet audio since the last loud packet, for the backstop
    private readonly Queue<byte[]> _lookbackBuffer = new(); // recent resampled audio for the open look-back
    private int _lookbackBytes;

    // Raised by the receive loop when the provider's VAD reports the user's turn ended; consumed
    // by the gate (running on the audio thread) to close it. Interlocked, as the two run
    // concurrently.
    private int _userSpeechEnded;

    // Names of the MCP servers whose tools have been loaded into the live session via a
    // use_<server> selector call. Starts empty (only the selectors are exposed) and grows as the
    // model asks for servers. Touched only from the receive loop (and once at startup, before it
    // begins consuming), so it needs no locking. Unused when the session cannot add tools
    // mid-session — then every server's tools are exposed up front instead.
    private readonly HashSet<string> _enabledServers = new();

    // Lifecycle: Active while bridging audio, Closing during the final close-out turn,
    // Disposed once torn down. Guarded by _stateLock.
    private enum State
    {
        Active,
        Closing,
        Disposed,
    }

    private readonly object _stateLock = new();
    private State _state = State.Active;
    private int _closingRaised;

    // Set once the receive loop has stopped, so a hand-off close knows the session is already
    // gone and there is nothing left to close out through.
    private volatile bool _receiveLoopEnded;

    // Set while the close-out turn runs, so end_conversation called during it is rejected
    // instead of trying to end an already-ended call.
    private volatile bool _closingOut;

    public Device Device { get; }

    public Conversation(
        Device device,
        AssistantConfiguration configuration,
        IAssistantSessionFactory sessionFactory,
        McpToolRegistry mcp,
        MemoryStore memory,
        AudioSender audioSender,
        UdpAudioServer audioServer,
        Action<Conversation> onClosing
    )
    {
        Device = device;
        _configuration = configuration;
        _memory = memory;
        _audioSender = audioSender;
        _audioServer = audioServer;
        _onClosing = onClosing;
        _deviceEndpoint = IPEndPoint.Parse(device.Configuration!.Endpoint!);

        _session = sessionFactory.CreateSession();
        _micResampler = new StreamingResampler(
            Constants.AudioFormat.SampleRate,
            _session.InputSampleRate
        );
        _outResampler = new StreamingResampler(
            _session.OutputSampleRate,
            Constants.AudioFormat.SampleRate
        );

        // Take the MCP lease last, once nothing above can throw: it keeps the shared MCP
        // connections open for this conversation's whole lifetime (through close-out) and is
        // released in DisposeResources. Past this point the registry is reached only via the lease.
        _mcpLease = mcp.Lease();
    }

    public async Task StartAsync()
    {
        CreateDebugWriters();

        await _session.StartAsync(
            new AssistantSessionOptions(BuildInstructions(), BuildTools()),
            _cts.Token
        );

        _audioServer.Data += OnAudioReceived;

        // Pace the assistant's audio out to the device in real time. The device's jitter buffer
        // is small and is overrun (garbled) if we forward the provider's faster-than-real-time
        // stream as-is; AudioStreaming is the same pacing the ring/doorbell playback uses.
        _ = RunAudioPump();

        // Only start consuming updates once setup succeeded, so a failed start never
        // raises the "ended" callback.
        _ = ReceiveLoop(_cts.Token);
    }

    /// <summary>Tears down the conversation without handing it off (used for a failed start).</summary>
    public void Dispose() => DisposeResources();

    /// <summary>
    /// Requests a graceful end of the conversation (idempotent). The live audio bridge stops and
    /// the conversation is handed off (via the closing callback) so the model can be given one
    /// final, audio-free turn to persist memory before the session is disposed.
    /// </summary>
    public void End() => RaiseClosing();

    // Raised exactly once, by whichever happens first: the user hanging up, the model ending the
    // conversation, or the receive loop stopping. Stops audio and hands the conversation off.
    private void RaiseClosing()
    {
        if (Interlocked.Exchange(ref _closingRaised, 1) != 0)
            return;

        // Stop bridging audio right away; the close-out turn is text-only and the owner is about
        // to free the device.
        StopAudio();
        _onClosing(this);
    }

    private void StopAudio()
    {
        _audioServer.Data -= OnAudioReceived;

        // Drop whatever is still buffered so playback stops abruptly (as on barge-in) instead of
        // draining out to the device after it has been reset, then end the stream.
        _audioOut.Discard();
        _audioOut.Complete();
    }

    /// <summary>
    /// Runs the final, audio-free close-out turn: the model is handed the free-form close-out
    /// prompt (which by default persists memory) with its tools still available, returning once
    /// that turn completes (or is cancelled). A no-op when the session is already gone (e.g. the
    /// socket dropped). Called by <see cref="ConversationCloser"/> after the device is freed.
    /// </summary>
    public async Task CloseOutAsync(CancellationToken cancellationToken)
    {
        if (_receiveLoopEnded)
            return;

        lock (_stateLock)
        {
            if (_state != State.Active)
                return;
            _state = State.Closing;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cts.Token
        );

        _closingOut = true;

        await _session.RunCloseOutTurnAsync(_configuration.CloseOutPrompt, linked.Token);
    }

    private void DisposeResources()
    {
        lock (_stateLock)
        {
            if (_state == State.Disposed)
                return;
            _state = State.Disposed;
        }

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
            _session.Dispose();
        }
        catch
        {
            // Ignore.
        }

        // Release the MCP lease. This conversation is the last to need it once it is being torn
        // down (the close-out turn, the only thing that runs after hand-over, has already finished
        // or timed out by now), so this is what lets the shared connections close.
        _mcpLease.Dispose();

        _cts.Dispose();

        lock (_writerLock)
        {
            try
            {
                _receivedWriter?.Dispose();
                _sentWriter?.Dispose();
                _micWriter?.Dispose();
                _modelInputWriter?.Dispose();
            }
            catch
            {
                // Ignore.
            }
            finally
            {
                _receivedWriter = null;
                _sentWriter = null;
                _micWriter = null;
                _modelInputWriter = null;
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
                Path.Combine(
                    directory,
                    $"{stamp}_{deviceId}_received_{_session.OutputSampleRate}.wav"
                ),
                new WaveFormat(_session.OutputSampleRate, 16, 1)
            );
            _sentWriter = new WaveFileWriter(
                Path.Combine(
                    directory,
                    $"{stamp}_{deviceId}_sent_{Constants.AudioFormat.SampleRate}.wav"
                ),
                new WaveFormat(Constants.AudioFormat.SampleRate, 16, 1)
            );
            _micWriter = new WaveFileWriter(
                Path.Combine(
                    directory,
                    $"{stamp}_{deviceId}_mic_{Constants.AudioFormat.SampleRate}.wav"
                ),
                new WaveFormat(Constants.AudioFormat.SampleRate, 16, 1)
            );
            // Exactly what we hand to the model as input audio (post-gate, post-resample), so we
            // can hear what the provider actually received and tell good gating from a corrupt
            // stream.
            _modelInputWriter = new WaveFileWriter(
                Path.Combine(
                    directory,
                    $"{stamp}_{deviceId}_model_input_{_session.InputSampleRate}.wav"
                ),
                new WaveFormat(_session.InputSampleRate, 16, 1)
            );

            Logger.Information(
                "Recording conversation audio for device {Device} to {Directory}",
                Device.DeviceId,
                directory
            );
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start conversation debug audio recording");
        }
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _session.ReceiveUpdatesAsync(cancellationToken))
            {
                switch (update)
                {
                    case OutputAudioUpdate audio:
                        var pcm16 = _outResampler.Resample(audio.Audio);

                        // Debug capture: the raw audio received from the provider and the
                        // resampled audio we send to the device.
                        lock (_writerLock)
                        {
                            _receivedWriter?.Write(audio.Audio, 0, audio.Audio.Length);
                            if (pcm16.Length > 0)
                                _sentWriter?.Write(pcm16, 0, pcm16.Length);
                        }

                        if (pcm16.Length > 0)
                            _audioOut.Append(pcm16);
                        break;

                    case OutputAudioEndedUpdate:
                        // The model finished this reply's audio; release any sub-pre-roll
                        // remainder so a short reply still plays out promptly.
                        _audioOut.Release();
                        break;

                    case ToolCallUpdate toolCall:
                        await HandleToolCall(toolCall, cancellationToken);
                        break;

                    case UserSpeechStartedUpdate:
                        // The user started speaking. The session interrupts the in-flight reply;
                        // drop the audio we have buffered so the assistant stops promptly instead
                        // of talking over the user.
                        _audioOut.Discard();
                        break;

                    case UserSpeechEndedUpdate:
                        // The provider's VAD reached the end of the user's turn. Signal the gate
                        // to close so we stop forwarding right when the model is about to respond
                        // (rather than guessing with a fixed hold).
                        Interlocked.Exchange(ref _userSpeechEnded, 1);
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
            Logger.Error(ex, "Assistant receive loop failed for device {Device}", Device.DeviceId);
        }
        finally
        {
            OnReceiveLoopEnded();
        }
    }

    private void OnReceiveLoopEnded()
    {
        _receiveLoopEnded = true;

        // The update stream stopped: provider hang-up, network error, or our own teardown after a
        // graceful close. Make sure the conversation is handed off so the device is freed
        // (idempotent — a no-op when a graceful close already did it), then dispose.
        RaiseClosing();
        DisposeResources();
    }

    private async Task HandleToolCall(ToolCallUpdate toolCall, CancellationToken cancellationToken)
    {
        var name = toolCall.Name;

        if (name == EndConversationTool)
        {
            if (_closingOut)
            {
                // We are already closing out, so there is nothing left to end. Rather than dropping
                // end_conversation from the close-out turn (which would mutate the tool set and
                // re-cost tokens), keep it and just report the error; the close-out turn continues.
                await _session.SubmitToolResultAsync(
                    toolCall.CallId,
                    "Error: the call has already ended.",
                    respond: true,
                    cancellationToken
                );
                return;
            }

            Logger.Information(
                "Model requested to end the conversation with {Device}.",
                Device.DeviceId
            );
            // respond: false — no live reply to the goodbye acknowledgement. End() hands the
            // conversation off for close-out, which cancels any in-flight response and runs the
            // model's wrap-up turn; a reply started here would only race that.
            await _session.SubmitToolResultAsync(
                toolCall.CallId,
                "Goodbye.",
                respond: false,
                cancellationToken
            );
            End();
            return;
        }

        if (_session.SupportsAddingTools && _mcpLease.TryResolveSelector(name, out var serverName))
        {
            string selectorOutput;
            if (_enabledServers.Contains(serverName))
            {
                selectorOutput = $"The '{serverName}' tools are already loaded.";
            }
            else
            {
                Logger.Information(
                    "Loading MCP server {Server} for {Device} (selector {Tool}).",
                    serverName,
                    Device.DeviceId,
                    name
                );
                _enabledServers.Add(serverName);
                await _session.AddToolsAsync(
                    _mcpLease.GetServerTools(serverName),
                    cancellationToken
                );
                selectorOutput =
                    $"Loaded the '{serverName}' tools. They are now available — call the one you need.";
            }

            await _session.SubmitToolResultAsync(
                toolCall.CallId,
                selectorOutput,
                respond: true,
                cancellationToken
            );
            return;
        }

        Logger.Information("Model called tool {Tool} with {Arguments}", name, toolCall.ArgumentsJson);

        string output;
        try
        {
            if (_memory.Handles(name))
                output = await _memory.CallAsync(name, toolCall.ArgumentsJson, cancellationToken);
            else
                output = await _mcpLease.CallAsync(name, toolCall.ArgumentsJson, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Tool {Tool} failed", name);
            output = $"The tool failed: {ex.Message}";
        }

        await _session.SubmitToolResultAsync(
            toolCall.CallId,
            output,
            respond: true,
            cancellationToken
        );
    }

    private async void OnAudioReceived(object? sender, UdpAudioDataEventArgs e)
    {
        if (_state != State.Active)
            return;

        // Identify this device's microphone stream by its source endpoint, and strip the
        // 4-byte sequence header (see AudioSender) before resampling.
        if (!e.RemoteEndpoint.Equals(_deviceEndpoint) || e.Data.Length <= 4)
            return;

        try
        {
            // Debug capture: the raw microphone audio as received from the device, before any
            // resampling or gating. Paired with the received/sent writers, this gives a basis
            // for tuning the gate threshold against the model's own (echoed) voice.
            lock (_writerLock)
                _micWriter?.Write(e.Data, 4, e.Data.Length - 4);

            // Resample to the session's input rate and run the experimental noise gate, which
            // forwards the mic only while the human is talking (with a short look-back so onsets
            // are not clipped) so the model does not pick up the device's echo of its own voice.
            // Null while the gate is closed — nothing is forwarded then.
            var resampled = GateMicAudio(e.Data.AsSpan(4));
            if (resampled is not { Length: > 0 })
                return;

            // Debug capture: the exact audio handed to the model, snippet by snippet.
            lock (_writerLock)
                _modelInputWriter?.Write(resampled, 0, resampled.Length);

            await _session.SendMicrophoneAudioAsync(resampled, _cts.Token);
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
            Logger.Debug(ex, "Failed to forward microphone audio to the assistant");
        }
    }

    // Resamples a mic packet to the session's input rate and runs the noise gate, returning the
    // audio to forward to the model — or null to forward nothing while the gate is closed. When
    // the gate is disabled (threshold <= 0) it just forwards the live resampled audio.
    //
    // The gate opens once it has seen MicGateAttackMs of continuous audio at or above the
    // threshold (so brief echo transients don't trip it), and on opening flushes the buffered
    // MicGateAttackMs + MicGatePrerollMs of recent audio so the utterance onset isn't clipped.
    //
    // While open it forwards everything live, so the provider's VAD sees a continuous stream and
    // can detect the end of the turn; it closes when the provider reports the user's turn ended.
    // MicGateHoldMs is only a safety backstop, force-closing the gate after that much continuous
    // quiet in case that event never arrives. Forwarding nothing (rather than silence) while
    // closed keeps the model's own echo out of its input.
    private byte[]? GateMicAudio(ReadOnlySpan<byte> mic)
    {
        // Always resample so the resampler stays continuous even across muted gaps.
        var resampled = _micResampler.Resample(mic);

        var threshold = _configuration.MicGateThreshold;
        if (threshold <= 0)
            return resampled; // gate disabled: forward live

        var sampleCount = mic.Length / 2;
        if (sampleCount == 0)
            return _gateOpen ? resampled : null;

        var sampleRate = Constants.AudioFormat.SampleRate;
        var attackSamples = (long)_configuration.MicGateAttackMs * sampleRate / 1000;
        var backstopSamples = (long)_configuration.MicGateHoldMs * sampleRate / 1000;
        var minQuietSamples = (long)SpeechEndedMinQuietMs * sampleRate / 1000;

        var rms = Rms(mic, sampleCount);
        if (rms >= threshold)
        {
            _consecutiveLoudSamples += sampleCount;
            _samplesSinceLoud = 0;
        }
        else
        {
            _consecutiveLoudSamples = 0;
            _samplesSinceLoud += sampleCount;
        }

        var wasOpen = _gateOpen;
        string? closeReason = null;
        if (!_gateOpen)
        {
            if (_consecutiveLoudSamples >= attackSamples)
            {
                _gateOpen = true;
                // Discard any speech-ended signal left over from the previous turn so it can't
                // close this freshly opened one.
                Interlocked.Exchange(ref _userSpeechEnded, 0);
            }
        }
        else if (Interlocked.Exchange(ref _userSpeechEnded, 0) == 1)
        {
            // Plausibility check (see SpeechEndedMinQuietMs): only close on the signal when the
            // mic has actually gone quiet; a loud mic means the user is still talking and the
            // signal — possibly inferred — is wrong, so it is consumed and dropped.
            if (_samplesSinceLoud >= minQuietSamples)
            {
                _gateOpen = false;
                closeReason = "user turn ended";
            }
        }
        else if (_configuration.MicGateHoldMs >= 0 && _samplesSinceLoud > backstopSamples)
        {
            // Backstop only; a negative MicGateHoldMs disables it (depend solely on VAD events).
            _gateOpen = false;
            closeReason = "backstop";
        }

        if (_gateOpen != wasOpen)
        {
            if (_gateOpen)
                Logger.Information(
                    "Mic gate started accepting audio for {Device} (rms {Rms:F0}, threshold {Threshold:F0})",
                    Device.DeviceId,
                    rms,
                    threshold
                );
            else
                Logger.Information(
                    "Mic gate stopped accepting audio for {Device} ({Reason})",
                    Device.DeviceId,
                    closeReason
                );
        }

        // Keep a rolling look-back of the most recent attack + pre-roll of audio, so that when the
        // gate opens we can flush the run-up that preceded detection.
        if (resampled.Length > 0)
        {
            _lookbackBuffer.Enqueue(resampled);
            _lookbackBytes += resampled.Length;
            var capBytes =
                (_configuration.MicGateAttackMs + _configuration.MicGatePrerollMs)
                * _session.InputSampleRate
                * 2
                / 1000;
            while (
                _lookbackBuffer.Count > 1
                && _lookbackBytes - _lookbackBuffer.Peek().Length >= capBytes
            )
                _lookbackBytes -= _lookbackBuffer.Dequeue().Length;
        }

        if (_gateOpen && !wasOpen)
            return FlushLookback(); // just opened: flush the buffered onset (includes this packet)
        if (_gateOpen)
            return resampled; // still open: forward live
        return null; // closed: forward nothing
    }

    // Concatenates and clears the look-back buffer; called when the gate opens.
    private byte[] FlushLookback()
    {
        var result = new byte[_lookbackBytes];
        var offset = 0;
        foreach (var chunk in _lookbackBuffer)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        _lookbackBuffer.Clear();
        _lookbackBytes = 0;
        return result;
    }

    private static double Rms(ReadOnlySpan<byte> pcm16, int sampleCount)
    {
        long sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            sumSquares += (long)sample * sample;
        }

        return Math.Sqrt((double)sumSquares / sampleCount);
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
            Logger.Error(ex, "Assistant audio pump failed for device {Device}", Device.DeviceId);
        }
    }

    // Substitutes dynamic placeholders in the instructions. Done per conversation so the
    // current date/time is fresh rather than frozen at server startup.
    private string BuildInstructions()
    {
        var culture = ResolveCulture(_configuration.Locale);
        var now = DateTime.Now.ToString("f", culture);
        var deviceName = Device.Configuration?.Device?.Name ?? Device.DeviceId;

        return _configuration
            .Instructions.Replace("{NOW}", now)
            .Replace("{MEMORIES}", _memory.RenderMemoriesList())
            .Replace("{DEVICE_NAME}", deviceName);
    }

    private static CultureInfo ResolveCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return CultureInfo.CurrentCulture;

        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            Logger.Warning(
                "Unknown ASSISTANT_LOCALE '{Locale}'; using {Culture}.",
                locale,
                CultureInfo.CurrentCulture.Name
            );
            return CultureInfo.CurrentCulture;
        }
    }

    // Builds the session's initial tool list: the memory tools, end_conversation, and the MCP
    // tools. When the session can add tools mid-session, MCP servers are loaded on demand to keep
    // the baseline context small: each server is exposed as a single use_<server> selector whose
    // call loads that server's actual tools (see HandleToolCall). Otherwise every server's tools
    // are exposed up front.
    private List<AssistantTool> BuildTools()
    {
        var tools = new List<AssistantTool>();

        if (_session.SupportsAddingTools)
            tools.AddRange(_mcpLease.GetServerSelectorTools());
        else
            tools.AddRange(_mcpLease.GetAllServerTools());

        tools.AddRange(_memory.GetTools());

        tools.Add(
            new AssistantTool(
                EndConversationTool,
                "End the conversation and hang up. Call this when the user says goodbye "
                    + "or no longer needs assistance.",
                BinaryData.FromString(
                    """{"type":"object","properties":{},"additionalProperties":false}"""
                )
            )
        );

        return tools;
    }
}
