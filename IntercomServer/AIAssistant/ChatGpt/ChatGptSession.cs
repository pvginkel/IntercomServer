// The OpenAI .NET Realtime API is shipped as an experimental (evaluation) surface and
// raises OPENAI002. We knowingly depend on it; suppress the diagnostic for this file.
#pragma warning disable OPENAI002

using System.Runtime.CompilerServices;
using OpenAI.Realtime;
using Serilog;

namespace IntercomServer.AIAssistant.ChatGpt;

/// <summary>
/// <see cref="IAssistantSession"/> implementation backed by the OpenAI Realtime API. Owns the
/// OpenAI-specific session mechanics: the session configuration (audio formats, server VAD,
/// voice), translating server updates into <see cref="AssistantUpdate"/>s, and the protocol
/// quirks the abstraction hides — most notably that OpenAI does not respond to tool output by
/// itself, so the turn is explicitly continued with one new response after the response that
/// emitted the call(s) completes.
///
/// The <c>web_search</c> tool is implemented natively here (a separate OpenAI Responses API
/// call, see <see cref="WebSearchTool"/>) and never surfaces as a <see cref="ToolCallUpdate"/>.
/// </summary>
internal sealed class ChatGptSession(ChatGptConfiguration configuration, WebSearchTool webSearch)
    : IAssistantSession
{
    private const int OpenAiSampleRate = 24000;

    private static readonly ILogger Logger = Log.ForContext<ChatGptSession>();

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // The session's current tool set (the conversation's tools plus our native web_search),
    // kept so AddToolsAsync can re-issue the full session configuration. Mutated only from the
    // receive loop's consumer, which is also what calls AddToolsAsync.
    private readonly List<AssistantTool> _tools = [];

    private string _instructions = "";
    private RealtimeSessionClient? _session;

    // Set when a tool result was submitted with respond: false (end_conversation), so the
    // response that emitted the call is not continued.
    private volatile bool _suppressContinuation;

    // Close-out bookkeeping. _closingOut is set while the close-out turn runs; _closeOutResponseIds
    // collects the responses created during it (touched only from the receive loop) so we can
    // tell its completion apart from a reply we cancelled on the way in.
    private volatile bool _closingOut;
    private readonly HashSet<string> _closeOutResponseIds = new();
    private readonly TaskCompletionSource _closeOutCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Set once the update stream has stopped, so a close-out started after that knows the
    // session is already gone and there is nothing left to close out through.
    private volatile bool _receiveEnded;

    public int InputSampleRate => OpenAiSampleRate;
    public int OutputSampleRate => OpenAiSampleRate;

    // Tools are added by re-issuing the session configuration (a session.update) with the
    // now-larger tool set; the model sees them on its next response.
    public bool SupportsAddingTools => true;

    public async Task StartAsync(
        AssistantSessionOptions options,
        CancellationToken cancellationToken
    )
    {
        _instructions = options.Instructions;
        _tools.AddRange(options.Tools);
        _tools.Add(WebSearchTool.GetTool());

        var client = new RealtimeClient(configuration.ApiKey!);
        var session = await client.StartConversationSessionAsync(
            configuration.Model,
            cancellationToken: cancellationToken
        );
        _session = session;

        await SendGuardedAsync(
            () => session.ConfigureConversationSessionAsync(BuildSessionOptions(), cancellationToken)
        );

        // Greet first so the device user hears something immediately.
        await SendGuardedAsync(() => session.StartResponseAsync(cancellationToken));
    }

    public async IAsyncEnumerable<AssistantUpdate> ReceiveUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var session = Session();

        try
        {
            await foreach (var update in session.ReceiveUpdatesAsync(cancellationToken))
            {
                switch (update)
                {
                    case RealtimeServerUpdateResponseOutputAudioDelta audio:
                        yield return new OutputAudioUpdate(audio.Delta.ToArray());
                        break;

                    case RealtimeServerUpdateResponseOutputAudioDone:
                        yield return new OutputAudioEndedUpdate();
                        break;

                    case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall:
                        var arguments = functionCall.FunctionArguments?.ToString() ?? "{}";

                        if (functionCall.FunctionName == WebSearchTool.ToolName)
                            await HandleWebSearchAsync(functionCall.CallId, arguments, cancellationToken);
                        else
                            yield return new ToolCallUpdate(
                                functionCall.CallId,
                                functionCall.FunctionName,
                                arguments
                            );
                        break;

                    case RealtimeServerUpdateResponseCreated created
                        when _closingOut && created.Response?.Id is { Length: > 0 } createdId:
                        // Track the responses belonging to the close-out turn so we can recognise
                        // its completion (and ignore a reply we cancelled on the way in).
                        _closeOutResponseIds.Add(createdId);
                        break;

                    case RealtimeServerUpdateResponseDone done:
                        await HandleResponseDoneAsync(done, cancellationToken);
                        break;

                    case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                        // Server VAD (interrupt_response) cancels any in-flight model response;
                        // the consumer drops its buffered audio.
                        yield return new UserSpeechStartedUpdate();
                        break;

                    case RealtimeServerUpdateInputAudioBufferSpeechStopped:
                        yield return new UserSpeechEndedUpdate();
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
            () => session.SendInputAudioAsync(BinaryData.FromBytes(audio), cancellationToken)
        );
    }

    public async Task SubmitToolResultAsync(
        string callId,
        string output,
        bool respond,
        CancellationToken cancellationToken
    )
    {
        if (!respond)
            _suppressContinuation = true;

        var session = Session();

        await SendGuardedAsync(
            () =>
                session.AddItemAsync(
                    new RealtimeFunctionCallOutputItem(callId, output),
                    cancellationToken
                )
        );

        // The turn is continued once, from HandleResponseDoneAsync, after the response that
        // emitted the call(s) completes — not here. Doing it per tool output makes parallel tool
        // calls each start a response, and all but the first fail with
        // conversation_already_has_active_response.
    }

    public async Task AddToolsAsync(
        IEnumerable<AssistantTool> tools,
        CancellationToken cancellationToken
    )
    {
        _tools.AddRange(tools);

        var session = Session();

        await SendGuardedAsync(
            () => session.ConfigureConversationSessionAsync(BuildSessionOptions(), cancellationToken)
        );
    }

    public async Task RunCloseOutTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_receiveEnded)
            return;

        var session = Session();

        _closingOut = true;

        // Cancel a half-finished reply (if any) so we can run our own close-out turn, then hand
        // the model the close-out prompt. The close-out responses inherit the session's current
        // tools, so the model keeps whatever it had loaded and can still load more.
        await TryCancelResponseAsync(session, cancellationToken);

        await SendGuardedAsync(
            () =>
                session.AddItemAsync(
                    RealtimeItem.CreateSystemMessageItem(prompt),
                    cancellationToken
                )
        );
        await SendGuardedAsync(
            () => session.StartResponseAsync(BuildCloseOutOptions(), cancellationToken)
        );

        await _closeOutCompleted.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            _session?.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }

    private RealtimeSessionClient Session() =>
        _session ?? throw new InvalidOperationException("The session has not been started.");

    // Runs when any response completes. A response that emitted function calls has had all its
    // tool outputs submitted by now (the consumer executes each ToolCallUpdate before pulling the
    // next update), so we continue the turn with exactly one response — close-out-aware, and
    // skipped when the model asked to hang up. Also detects completion of the close-out turn.
    private async Task HandleResponseDoneAsync(
        RealtimeServerUpdateResponseDone done,
        CancellationToken cancellationToken
    )
    {
        var session = Session();

        var hadFunctionCall =
            done.Response?.OutputItems.Any(item => item is RealtimeFunctionCallItem) == true;

        if (hadFunctionCall && !_suppressContinuation)
        {
            // Continue the turn. During the close-out turn, keep continuations text-only
            // (BuildCloseOutOptions) instead of the spoken default.
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

    private async Task HandleWebSearchAsync(
        string callId,
        string arguments,
        CancellationToken cancellationToken
    )
    {
        string output;
        try
        {
            output = await webSearch.SearchAsync(arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Web search failed");
            output = $"The tool failed: {ex.Message}";
        }

        await SubmitToolResultAsync(callId, output, respond: true, cancellationToken);
    }

    private async Task TryCancelResponseAsync(
        RealtimeSessionClient session,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await SendGuardedAsync(() => session.CancelResponseAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            // Usually there is simply no active response to cancel; that is fine.
            Logger.Debug(ex, "No in-flight response to cancel while closing the session");
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
                    Voice = configuration.Voice,
                },
            },
        };

        options.OutputModalities.Add(RealtimeOutputModality.Audio);

        foreach (var tool in _tools)
        {
            options.Tools.Add(
                new RealtimeFunctionTool(tool.Name)
                {
                    FunctionDescription = tool.Description,
                    FunctionParameters = tool.ParametersJson,
                }
            );
        }

        return options;
    }

    // The close-out turn differs from a live turn only in being text-only (no spoken reply, which
    // also avoids paying for audio output the freed device would never hear). It deliberately does
    // NOT set its own tool list: leaving Tools unset makes the response inherit the session's
    // current tools, so no tool schemas are re-sent (keeping token use down) and the model keeps
    // whatever servers it had loaded.
    private RealtimeResponseOptions BuildCloseOutOptions()
    {
        var options = new RealtimeResponseOptions();
        options.OutputModalities.Add(RealtimeOutputModality.Text);
        return options;
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
