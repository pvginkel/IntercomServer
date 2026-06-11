namespace IntercomServer.AIAssistant;

/// <summary>
/// One live, bidirectional voice session with an AI provider (OpenAI Realtime, Gemini Live,
/// ...). The session owns everything provider-specific: the wire protocol, session
/// configuration, voice and model selection, turn continuation after tool output, and any
/// tools the provider implements natively. The owning <see cref="Conversation"/> owns
/// everything device-specific: audio bridging, the mic gate, and executing the shared
/// function tools (MCP, memory, end_conversation).
///
/// A session is cheap to construct (see <see cref="IAssistantSessionFactory.CreateSession"/>)
/// and connects in <see cref="StartAsync"/>. It is driven by a single consumer: one receive
/// loop iterating <see cref="ReceiveUpdatesAsync"/>, which executes tool calls inline, plus
/// microphone audio arriving concurrently via <see cref="SendMicrophoneAudioAsync"/>.
/// </summary>
internal interface IAssistantSession : IDisposable
{
    /// <summary>The sample rate (mono PCM16) the provider expects microphone audio in.</summary>
    int InputSampleRate { get; }

    /// <summary>The sample rate (mono PCM16) the provider produces output audio in.</summary>
    int OutputSampleRate { get; }

    /// <summary>
    /// Whether tools can be added to the session while it is live
    /// (<see cref="AddToolsAsync"/>). When false, the conversation exposes every tool up
    /// front instead of loading MCP servers on demand.
    /// </summary>
    bool SupportsAddingTools { get; }

    /// <summary>
    /// Connects to the provider, configures the session (instructions, tools, audio, voice
    /// activity detection) and asks the assistant to greet the user.
    /// </summary>
    Task StartAsync(AssistantSessionOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// The session's update stream. Ends when the provider closes the connection, the
    /// session is disposed, or a fatal error occurs (which is thrown to the consumer).
    /// </summary>
    IAsyncEnumerable<AssistantUpdate> ReceiveUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>Forwards microphone audio: mono PCM16 at <see cref="InputSampleRate"/>.</summary>
    Task SendMicrophoneAudioAsync(byte[] audio, CancellationToken cancellationToken);

    /// <summary>
    /// Reports the outcome of a <see cref="ToolCallUpdate"/>. When <paramref name="respond"/>
    /// is true the session makes sure the assistant produces a reply to the output (however
    /// its provider requires that). Pass false only for the end_conversation acknowledgement:
    /// the conversation is about to be handed off for close-out, and a live spoken reply in
    /// between would race it — the model still gets its wrap-up via
    /// <see cref="RunCloseOutTurnAsync"/>.
    /// </summary>
    Task SubmitToolResultAsync(
        string callId,
        string output,
        bool respond,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Adds tools to the live session; the assistant sees them on its next response. Only
    /// valid when <see cref="SupportsAddingTools"/> is true.
    /// </summary>
    Task AddToolsAsync(IEnumerable<AssistantTool> tools, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the final close-out turn after the live audio bridge has stopped: cancels any
    /// in-flight reply, hands the assistant <paramref name="prompt"/> and lets it finish —
    /// without producing audio, where the provider allows that — making tool calls as needed
    /// (they still arrive through <see cref="ReceiveUpdatesAsync"/>, so the receive loop must
    /// keep running). Returns when the turn completes; a no-op when the session is already
    /// gone.
    /// </summary>
    Task RunCloseOutTurnAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>The provider-neutral part of a session's configuration.</summary>
internal sealed record AssistantSessionOptions(
    string Instructions,
    IReadOnlyList<AssistantTool> Tools
);
