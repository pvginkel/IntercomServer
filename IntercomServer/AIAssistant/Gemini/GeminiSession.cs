using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using Serilog;

namespace IntercomServer.AIAssistant.Gemini;

/// <summary>
/// <see cref="IAssistantSession"/> implementation backed by the Google Live API (Gemini),
/// via the official Google GenAI SDK. Owns the Gemini-specific session mechanics and how the
/// provider's behavior differs from the abstraction's reference points:
///
/// <list type="bullet">
/// <item>Web search is Gemini's native Google Search grounding tool — no function tool, no
/// extra round-trip.</item>
/// <item>The tool set is fixed at session setup (<see cref="SupportsAddingTools"/> is false),
/// so the conversation exposes every MCP tool up front instead of loading on demand.</item>
/// <item>The model resumes generating by itself after a tool response, so
/// <c>respond: false</c> needs no work here (no turn continuation exists to suppress).</item>
/// <item>Gemini reports no end-of-user-turn event, so <see cref="UserSpeechEndedUpdate"/> is
/// inferred instead: the model starting to speak means its VAD decided the user's turn
/// ended. The consumer treats the inference with suspicion when the mic is loud at that
/// moment (see the mic gate), and the gate's hold-time backstop still applies.</item>
/// <item>The response modality is fixed at setup, so the close-out turn cannot be made
/// text-only; its audio is suppressed here instead (it is generated, but never surfaces).</item>
/// </list>
///
/// Audio in is 16 kHz mono PCM16 (matching the intercom's native rate, so the mic resampler
/// passes through); audio out is 24 kHz.
/// </summary>
internal sealed class GeminiSession(GeminiConfiguration configuration) : IAssistantSession
{
    private const string InputMimeType = "audio/pcm;rate=16000";

    private static readonly ILogger Logger = Log.ForContext<GeminiSession>();

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Client? _client;
    private AsyncSession? _session;

    // The tool calls yielded to the consumer whose responses have not been submitted yet, by
    // the call id we yielded. Kept because Gemini's FunctionResponse must repeat the function
    // name alongside the id, while SubmitToolResultAsync only receives the id. Touched only by
    // the receive loop's consumer (which executes tool calls between pulls), so no locking.
    private readonly Dictionary<string, FunctionCall> _pendingCalls = new();

    // Close-out bookkeeping. Gemini has no response ids, so completion is detected
    // heuristically: once the close-out prompt has been sent (_closingOut), the first model
    // activity (tool call or generation-complete) marks the close-out turn as underway, and
    // the next turn-complete with no outstanding tool calls finishes it. A turn-complete
    // arriving before any activity is attributed to the reply the close-out prompt cancelled.
    private volatile bool _closingOut;
    private bool _closeOutTurnSeen;
    private readonly TaskCompletionSource _closeOutCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Set once the update stream has stopped, so a close-out started after that knows the
    // session is already gone and there is nothing left to close out through.
    private volatile bool _receiveEnded;

    // Whether the model is currently speaking (between a reply's first audio chunk and its
    // generation completing or being interrupted). Used to emit UserSpeechEndedUpdate exactly
    // once per reply: the model starting to speak is the implicit signal that its VAD decided
    // the user's turn ended. Touched only from the receive loop.
    private bool _modelSpeaking;

    public int InputSampleRate => 16000;
    public int OutputSampleRate => 24000;

    // The Live API takes its tool set once, in the connection setup, and has no equivalent of
    // a session update to extend it afterwards.
    public bool SupportsAddingTools => false;

    public async Task StartAsync(
        AssistantSessionOptions options,
        CancellationToken cancellationToken
    )
    {
        _client = new Client(apiKey: configuration.ApiKey);

        var config = new LiveConnectConfig
        {
            SystemInstruction = new Content { Parts = [Part.FromText(options.Instructions)] },
            ResponseModalities = [Modality.Audio],
            SpeechConfig = new SpeechConfig
            {
                VoiceConfig = new VoiceConfig
                {
                    PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                    {
                        VoiceName = configuration.Voice,
                    },
                },
            },
            Tools = BuildTools(options.Tools),
        };

        // No greeting is requested: unlike the ChatGPT provider, the session stays silent
        // until the user asks something.
        _session = await _client.Live.ConnectAsync(configuration.Model, config, cancellationToken);
    }

    public async IAsyncEnumerable<AssistantUpdate> ReceiveUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var session = Session();

        try
        {
            while (true)
            {
                var message = await session.ReceiveAsync(cancellationToken);
                if (message == null)
                    break;

                if (message.ToolCall?.FunctionCalls is { } functionCalls)
                {
                    if (_closingOut)
                        _closeOutTurnSeen = true;

                    foreach (var call in functionCalls)
                    {
                        var name = call.Name ?? "";
                        var callId = call.Id ?? name;
                        _pendingCalls[callId] = call;

                        var arguments =
                            call.Args == null ? "{}" : JsonSerializer.Serialize(call.Args);
                        yield return new ToolCallUpdate(callId, name, arguments);
                    }
                }

                if (message.ServerContent is { } content)
                {
                    if (content.Interrupted == true)
                    {
                        _modelSpeaking = false;
                        yield return new UserSpeechStartedUpdate();
                    }

                    // The close-out turn's audio is generated regardless (the modality is fixed
                    // at setup) but never surfaced: the device has already been freed.
                    if (!_closingOut && content.ModelTurn?.Parts is { } parts)
                    {
                        foreach (var part in parts)
                        {
                            if (part.InlineData?.Data is not { Length: > 0 } audio)
                                continue;

                            // The reply's first audio means the model's VAD decided the user's
                            // turn ended — Gemini's substitute for an explicit speech-ended event.
                            if (!_modelSpeaking)
                            {
                                _modelSpeaking = true;
                                yield return new UserSpeechEndedUpdate();
                            }

                            yield return new OutputAudioUpdate(audio);
                        }
                    }

                    if (content.GenerationComplete == true)
                    {
                        _modelSpeaking = false;

                        if (_closingOut)
                            _closeOutTurnSeen = true;

                        yield return new OutputAudioEndedUpdate();
                    }

                    // A close-out turn that completes with no outstanding tool calls means the
                    // close-out is done (see the field comments for the attribution heuristic).
                    if (
                        content.TurnComplete == true
                        && _closingOut
                        && _closeOutTurnSeen
                        && _pendingCalls.Count == 0
                    )
                        _closeOutCompleted.TrySetResult();
                }

                if (message.GoAway != null)
                    Logger.Warning("The Gemini session announced it is about to close (GoAway).");
            }
        }
        finally
        {
            _receiveEnded = true;

            // Unblock a close-out turn that will never complete now the stream has stopped.
            _closeOutCompleted.TrySetResult();
        }
    }

    public async Task SendMicrophoneAudioAsync(byte[] audio, CancellationToken cancellationToken)
    {
        var session = Session();

        await SendGuardedAsync(
            () =>
                session.SendRealtimeInputAsync(
                    new LiveSendRealtimeInputParameters
                    {
                        Audio = new Blob { Data = audio, MimeType = InputMimeType },
                    },
                    cancellationToken
                )
        );
    }

    public async Task SubmitToolResultAsync(
        string callId,
        string output,
        bool respond,
        CancellationToken cancellationToken
    )
    {
        _pendingCalls.Remove(callId, out var call);

        // The respond flag needs no work here: Gemini resumes generating after a tool response
        // by itself, and there is no per-call way to suppress that. For end_conversation
        // (respond: false) the unwanted reply is harmless — the audio bridge has already been
        // stopped by the time it is generated, so it is never heard.
        var session = Session();

        await SendGuardedAsync(
            () =>
                session.SendToolResponseAsync(
                    new LiveSendToolResponseParameters
                    {
                        FunctionResponses =
                        [
                            new FunctionResponse
                            {
                                Id = call?.Id,
                                Name = call?.Name,
                                Response = new Dictionary<string, object> { ["output"] = output },
                            },
                        ],
                    },
                    cancellationToken
                )
        );
    }

    public Task AddToolsAsync(
        IEnumerable<AssistantTool> tools,
        CancellationToken cancellationToken
    ) =>
        throw new NotSupportedException(
            "The Gemini Live API cannot add tools to a running session."
        );

    public async Task RunCloseOutTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_receiveEnded)
            return;

        var session = Session();

        _closingOut = true;

        // Sending new client content cancels any in-flight reply, so this both interrupts a
        // half-finished spoken answer and starts the close-out turn. The prompt is sent as a
        // user turn — Live API client content only knows the user and model roles.
        await SendGuardedAsync(
            () =>
                session.SendClientContentAsync(
                    new LiveSendClientContentParameters
                    {
                        Turns = [new Content { Role = "user", Parts = [Part.FromText(prompt)] }],
                        TurnComplete = true,
                    },
                    cancellationToken
                )
        );

        await _closeOutCompleted.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            _session?.CloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }

    private AsyncSession Session() =>
        _session ?? throw new InvalidOperationException("The session has not been started.");

    // The session's tools: the conversation's function tools plus Gemini's native Google
    // Search grounding, which replaces the ChatGPT provider's web_search function tool.
    private static List<Tool> BuildTools(IReadOnlyList<AssistantTool> tools)
    {
        var declarations = tools
            .Select(tool => new FunctionDeclaration
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersJsonSchema = JsonSerializer.Deserialize<JsonElement>(
                    tool.ParametersJson
                ),
            })
            .ToList();

        return [new Tool { GoogleSearch = new GoogleSearch() }, new Tool { FunctionDeclarations = declarations }];
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
}
