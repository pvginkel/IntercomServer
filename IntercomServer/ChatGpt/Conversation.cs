// The OpenAI .NET Realtime API is shipped as an experimental (evaluation) surface and
// raises OPENAI002. We knowingly depend on it; suppress the diagnostic for this file.
#pragma warning disable OPENAI002

using System.Globalization;
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

    // Buffer this much of the model's audio before playback starts, to ride out OpenAI's
    // bursty delivery without starving the device's jitter buffer at the start of a reply.
    private const double PrerollSeconds = 0.15;

    private static readonly ILogger Logger = Log.ForContext<Conversation>();

    private readonly ChatGptConfiguration _configuration;
    private readonly McpLease _mcpLease;
    private readonly WebSearchTool _webSearch;
    private readonly MemoryStore _memory;
    private readonly AudioSender _audioSender;
    private readonly UdpAudioServer _audioServer;
    private readonly Action<Conversation> _onClosing;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly IPEndPoint _deviceEndpoint;
    private readonly StreamingResampler _micResampler =
        new(Constants.AudioFormat.SampleRate, OpenAiSampleRate);
    private readonly StreamingResampler _outResampler =
        new(OpenAiSampleRate, Constants.AudioFormat.SampleRate);
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
    private readonly Queue<byte[]> _lookbackBuffer = new(); // recent 24 kHz audio for the open look-back
    private int _lookbackBytes;

    // Raised by the receive loop when the server's VAD reports the user's turn ended; consumed by
    // the gate (running on the audio thread) to close it. Interlocked, as the two run concurrently.
    private int _serverSpeechStopped;

    private RealtimeSessionClient? _session;

    // Names of the MCP servers whose tools have been loaded into the live session via a
    // use_<server> selector call. Starts empty (only the selectors are exposed) and grows as the
    // model asks for servers. Touched only from the receive loop (and once at startup, before it
    // begins consuming), so it needs no locking.
    private readonly HashSet<string> _enabledServers = new();

    // The system prompt, resolved once at session start. Cached so re-issuing the session
    // configuration to add tools (EnableServerAsync) does not re-substitute {NOW}/{MEMORIES}.
    private string _instructions = "";

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

    // Close-out bookkeeping. _closingOut is set while the close-out turn runs; _closeOutResponseIds
    // collects the responses created during it (touched only from the receive loop) so we can
    // tell its completion apart from a reply we cancelled on the way in.
    private volatile bool _closingOut;
    private readonly HashSet<string> _closeOutResponseIds = new();
    private readonly TaskCompletionSource _closeOutCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Set when the model calls end_conversation, so the turn that did so isn't continued.
    private volatile bool _endRequested;

    public Device Device { get; }

    public Conversation(
        Device device,
        ChatGptConfiguration configuration,
        McpToolRegistry mcp,
        WebSearchTool webSearch,
        MemoryStore memory,
        AudioSender audioSender,
        UdpAudioServer audioServer,
        Action<Conversation> onClosing
    )
    {
        Device = device;
        _configuration = configuration;
        _webSearch = webSearch;
        _memory = memory;
        _audioSender = audioSender;
        _audioServer = audioServer;
        _onClosing = onClosing;
        _deviceEndpoint = IPEndPoint.Parse(device.Configuration!.Endpoint!);

        // Take the MCP lease last, once nothing above can throw: it keeps the shared MCP
        // connections open for this conversation's whole lifetime (through close-out) and is
        // released in DisposeResources. Past this point the registry is reached only via the lease.
        _mcpLease = mcp.Lease();
    }

    public async Task StartAsync()
    {
        var realtimeClient = new RealtimeClient(_configuration.ApiKey!);
        var session = await realtimeClient.StartConversationSessionAsync(
            _configuration.Model,
            cancellationToken: _cts.Token
        );
        _session = session;
        _instructions = BuildInstructions();

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
    /// prompt (which by default persists memory) with its memory tools available, returning once
    /// that turn completes (or is cancelled). A no-op when the session is already gone (e.g. the
    /// socket dropped). Called by <see cref="ConversationCloser"/> after the device is freed.
    /// </summary>
    public async Task CloseOutAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session == null || _receiveLoopEnded)
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
        var token = linked.Token;

        _closingOut = true;

        // Cancel a half-finished reply (if any) so we can run our own close-out turn, then hand the
        // model the close-out prompt with only the memory tools — no audio, and no MCP, whose
        // connections are released the moment the conversation is handed over.
        await TryCancelResponseAsync(session, token);

        await SendGuardedAsync(
            () =>
                session.AddItemAsync(
                    RealtimeItem.CreateSystemMessageItem(_configuration.CloseOutPrompt),
                    token
                )
        );
        await SendGuardedAsync(() => session.StartResponseAsync(BuildCloseOutOptions(), token));

        await _closeOutCompleted.Task.WaitAsync(token);
    }

    private async Task TryCancelResponseAsync(
        RealtimeSessionClient session,
        CancellationToken token
    )
    {
        try
        {
            await SendGuardedAsync(() => session.CancelResponseAsync(token));
        }
        catch (Exception ex)
        {
            // Usually there is simply no active response to cancel; that is fine.
            Logger.Debug(
                ex,
                "No in-flight response to cancel while closing {Device}",
                Device.DeviceId
            );
        }
    }

    // The close-out turn differs from a live turn only in being text-only (no spoken reply, which
    // also avoids paying for audio output the freed device would never hear). It deliberately does
    // NOT set its own tool list: leaving Tools unset makes the response inherit the session's
    // current tools, so no tool schemas are re-sent (keeping token use down) and the model keeps
    // whatever servers it had loaded. The MCP lease is still held, so it can load another server
    // (use_<server>) and finish a deferred request after hang-up — e.g. actually send the email the
    // user asked for on their way out. end_conversation stays available too; see HandleFunctionCall.
    private RealtimeResponseOptions BuildCloseOutOptions()
    {
        var options = new RealtimeResponseOptions();
        options.OutputModalities.Add(RealtimeOutputModality.Text);
        return options;
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
            _session?.Dispose();
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
            _micWriter = new WaveFileWriter(
                Path.Combine(
                    directory,
                    $"{stamp}_{deviceId}_mic_{Constants.AudioFormat.SampleRate}.wav"
                ),
                new WaveFormat(Constants.AudioFormat.SampleRate, 16, 1)
            );
            // Exactly what we hand to the model as input audio (post-gate, post-resample), so we
            // can hear what OpenAI actually received and tell good gating from a corrupt stream.
            _modelInputWriter = new WaveFileWriter(
                Path.Combine(directory, $"{stamp}_{deviceId}_model_input_{OpenAiSampleRate}.wav"),
                new WaveFormat(OpenAiSampleRate, 16, 1)
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

                    case RealtimeServerUpdateResponseOutputAudioDone:
                        // The model finished this reply's audio; release any sub-pre-roll
                        // remainder so a short reply still plays out promptly.
                        _audioOut.Release();
                        break;

                    case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall:
                        await HandleFunctionCall(session, functionCall, cancellationToken);
                        break;

                    case RealtimeServerUpdateResponseCreated created
                        when _closingOut && created.Response?.Id is { Length: > 0 } createdId:
                        // Track the responses belonging to the close-out turn so we can recognise
                        // its completion (and ignore a reply we cancelled on the way in).
                        _closeOutResponseIds.Add(createdId);
                        break;

                    case RealtimeServerUpdateResponseDone done:
                        await HandleResponseDone(session, done, cancellationToken);
                        break;

                    case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                        // The user started speaking. Server VAD (interrupt_response) cancels
                        // any in-flight model response; drop the audio we have buffered so the
                        // assistant stops promptly instead of talking over the user.
                        _audioOut.Discard();
                        break;

                    case RealtimeServerUpdateInputAudioBufferSpeechStopped:
                        // Server VAD reached the end of the user's turn. Signal the gate to close so
                        // we stop forwarding right when the model is about to respond (rather than
                        // guessing with a fixed hold).
                        Interlocked.Exchange(ref _serverSpeechStopped, 1);
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
            OnReceiveLoopEnded();
        }
    }

    private void OnReceiveLoopEnded()
    {
        _receiveLoopEnded = true;

        // Unblock a close-out turn that will never complete now the stream has stopped.
        _closeOutCompleted.TrySetResult();

        // The update stream stopped: server hang-up, network error, or our own teardown after a
        // graceful close. Make sure the conversation is handed off so the device is freed
        // (idempotent — a no-op when a graceful close already did it), then dispose.
        RaiseClosing();
        DisposeResources();
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
            if (_closingOut)
            {
                // We are already closing out, so there is nothing left to end. Rather than dropping
                // end_conversation from the close-out turn (which would mutate the tool set and
                // re-cost tokens), keep it and just report the error; the close-out turn continues.
                await SendGuardedAsync(
                    () =>
                        session.AddItemAsync(
                            new RealtimeFunctionCallOutputItem(
                                functionCall.CallId,
                                "Error: the call has already ended."
                            ),
                            cancellationToken
                        )
                );
                return;
            }

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
            _endRequested = true;
            End();
            return;
        }

        if (_mcpLease.TryResolveSelector(name, out var serverName))
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
                await EnableServerAsync(serverName, cancellationToken);
                selectorOutput =
                    $"Loaded the '{serverName}' tools. They are now available — call the one you need.";
            }

            await SendGuardedAsync(
                () =>
                    session.AddItemAsync(
                        new RealtimeFunctionCallOutputItem(functionCall.CallId, selectorOutput),
                        cancellationToken
                    )
            );
            return;
        }

        var arguments = functionCall.FunctionArguments?.ToString() ?? "{}";
        Logger.Information("Model called tool {Tool} with {Arguments}", name, arguments);

        string output;
        try
        {
            if (_memory.Handles(name))
                output = await _memory.CallAsync(name, arguments, cancellationToken);
            else if (name == WebSearchTool.ToolName)
                output = await _webSearch.SearchAsync(arguments, cancellationToken);
            else
                output = await _mcpLease.CallAsync(name, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Tool {Tool} failed", name);
            output = $"The tool failed: {ex.Message}";
        }

        await SendGuardedAsync(
            () =>
                session.AddItemAsync(
                    new RealtimeFunctionCallOutputItem(functionCall.CallId, output),
                    cancellationToken
                )
        );

        // The turn is continued once, from HandleResponseDone, after the response that emitted the
        // call(s) completes — not here. Doing it per tool output makes parallel tool calls each
        // start a response, and all but the first fail with conversation_already_has_active_response.
    }

    // Runs when any response completes. A response that emitted function calls has had all its tool
    // outputs submitted by now (during the preceding ArgumentsDone events), so we continue the turn
    // with exactly one response — close-out-aware, and skipped when the model asked to hang up.
    // Also detects completion of the close-out turn.
    private async Task HandleResponseDone(
        RealtimeSessionClient session,
        RealtimeServerUpdateResponseDone done,
        CancellationToken cancellationToken
    )
    {
        var hadFunctionCall =
            done.Response?.OutputItems.Any(item => item is RealtimeFunctionCallItem) == true;

        if (hadFunctionCall && !_endRequested)
        {
            // Continue the turn. During the close-out turn, keep continuations text-only and
            // limited to the memory tools (BuildCloseOutOptions) instead of the spoken default.
            await SendGuardedAsync(
                () =>
                    _closingOut
                        ? session.StartResponseAsync(BuildCloseOutOptions(), cancellationToken)
                        : session.StartResponseAsync(cancellationToken)
            );
            return;
        }

        // A close-out response that finished without making another tool call means the close-out
        // turn is done.
        if (
            _closingOut
            && done.Response?.Id is { Length: > 0 } doneId
            && _closeOutResponseIds.Contains(doneId)
        )
            _closeOutCompleted.TrySetResult();
    }

    private async void OnAudioReceived(object? sender, UdpAudioDataEventArgs e)
    {
        var session = _session;
        if (session == null || _state != State.Active)
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

            // Resample to 24 kHz and run the experimental noise gate, which forwards the mic only
            // while the human is talking (with a short look-back so onsets are not clipped) so the
            // model does not pick up the device's echo of its own voice. Null while the gate is
            // closed — nothing is forwarded then.
            var pcm24 = GateMicAudio(e.Data.AsSpan(4));
            if (pcm24 is not { Length: > 0 })
                return;

            // Debug capture: the exact audio handed to the model, snippet by snippet.
            lock (_writerLock)
                _modelInputWriter?.Write(pcm24, 0, pcm24.Length);

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

    // Resamples a mic packet to 24 kHz and runs the noise gate, returning the audio to forward to
    // the model — or null to forward nothing while the gate is closed. When the gate is disabled
    // (threshold <= 0) it just forwards the live resampled audio.
    //
    // The gate opens once it has seen MicGateAttackMs of continuous audio at or above the
    // threshold (so brief echo transients don't trip it), and on opening flushes the buffered
    // MicGateAttackMs + MicGatePrerollMs of recent audio so the utterance onset isn't clipped.
    //
    // While open it forwards everything live, so the server's VAD sees a continuous stream and can
    // detect the end of the turn; it closes when the server reports speech-stopped. MicGateHoldMs
    // is only a safety backstop, force-closing the gate after that much continuous quiet in case
    // the speech-stopped event never arrives. Forwarding nothing (rather than silence) while closed
    // keeps the model's own echo out of its input.
    private byte[]? GateMicAudio(ReadOnlySpan<byte> mic)
    {
        // Always resample so the resampler stays continuous even across muted gaps.
        var pcm24 = _micResampler.Resample(mic);

        var threshold = _configuration.MicGateThreshold;
        if (threshold <= 0)
            return pcm24; // gate disabled: forward live

        var sampleCount = mic.Length / 2;
        if (sampleCount == 0)
            return _gateOpen ? pcm24 : null;

        var sampleRate = Constants.AudioFormat.SampleRate;
        var attackSamples = (long)_configuration.MicGateAttackMs * sampleRate / 1000;
        var backstopSamples = (long)_configuration.MicGateHoldMs * sampleRate / 1000;

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
                // Discard any speech-stopped left over from the previous turn so it can't close
                // this freshly opened one.
                Interlocked.Exchange(ref _serverSpeechStopped, 0);
            }
        }
        else if (Interlocked.Exchange(ref _serverSpeechStopped, 0) == 1)
        {
            _gateOpen = false;
            closeReason = "server speech-stopped";
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
        if (pcm24.Length > 0)
        {
            _lookbackBuffer.Enqueue(pcm24);
            _lookbackBytes += pcm24.Length;
            var capBytes =
                (_configuration.MicGateAttackMs + _configuration.MicGatePrerollMs)
                * OpenAiSampleRate
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
            return pcm24; // still open: forward live
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
            Logger.Error(ex, "ChatGPT audio pump failed for device {Device}", Device.DeviceId);
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
                "Unknown CHATGPT_LOCALE '{Locale}'; using {Culture}.",
                locale,
                CultureInfo.CurrentCulture.Name
            );
            return CultureInfo.CurrentCulture;
        }
    }

    private RealtimeConversationSessionOptions BuildSessionOptions()
    {
        var options = new RealtimeConversationSessionOptions
        {
            Instructions = _instructions,
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

        AddTools(options);

        return options;
    }

    // Populates the session's tool list: the always-present tools plus one use_<server> selector
    // per MCP server, plus the actual tools of any server already loaded this conversation. MCP
    // tools are loaded on demand (see EnableServerAsync) to keep the baseline context small.
    private void AddTools(RealtimeConversationSessionOptions options)
    {
        foreach (var tool in _mcpLease.GetServerSelectorTools())
            options.Tools.Add(tool);

        foreach (var server in _enabledServers)
        foreach (var tool in _mcpLease.GetServerTools(server))
            options.Tools.Add(tool);

        foreach (var tool in _memory.GetRealtimeTools())
            options.Tools.Add(tool);

        options.Tools.Add(BuildWebSearchTool());
        options.Tools.Add(BuildEndConversationTool());
    }

    // Loads a server's tools into the live session in response to a use_<server> selector call, by
    // re-issuing the session configuration (a session.update) with the now-larger tool set. The
    // model sees the new tools on its next response — driven by the turn continuation in
    // HandleResponseDone after the calling response completes. This works during close-out too: the
    // close-out responses inherit the session tools, so a server loaded then becomes callable.
    private async Task EnableServerAsync(string serverName, CancellationToken cancellationToken)
    {
        _enabledServers.Add(serverName);

        var session = _session;
        if (session != null)
            await SendGuardedAsync(
                () => session.ConfigureConversationSessionAsync(BuildSessionOptions(), cancellationToken)
            );
    }

    private static RealtimeFunctionTool BuildWebSearchTool() =>
        new(WebSearchTool.ToolName)
        {
            FunctionDescription =
                "Search the web for current or factual information and return a concise answer. "
                + "Use this for recent events, or anything you are unsure about.",
            FunctionParameters = BinaryData.FromString(
                """{"type":"object","properties":{"query":{"type":"string","description":"What to search the web for."}},"required":["query"],"additionalProperties":false}"""
            ),
        };

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
