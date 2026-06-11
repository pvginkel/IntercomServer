namespace IntercomServer.AIAssistant;

/// <summary>
/// Provider-neutral configuration for the AI assistant feature. Populated from environment
/// variables in <c>Program.cs</c>, mirroring how <see cref="ServerConfiguration"/> is loaded.
/// Provider-specific settings (API key, model, voice) live with the provider, e.g.
/// <see cref="ChatGpt.ChatGptConfiguration"/>.
/// </summary>
internal class AssistantConfiguration
{
    /// <summary>
    /// Culture used to format dynamic values substituted into the instructions, such as the
    /// <c>{NOW}</c> placeholder (e.g. <c>nl-NL</c>). When empty, the host's current culture.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>System instructions / persona handed to the model on session start.</summary>
    public string Instructions { get; init; } =
        "You are a helpful, friendly voice assistant built into a home intercom. "
        + "Keep your answers short and conversational, suitable for being spoken aloud. "
        + "When the user says goodbye or clearly wants to stop, call the end_conversation tool to hang up.";

    /// <summary>
    /// Free-form instruction handed to the model on the final, audio-free turn when a conversation
    /// ends (the "close-out" turn). It can say anything: persist memory, finish a deferred request
    /// such as sending an email, etc. There is deliberately no built-in default — it is
    /// <b>required</b> when the feature is enabled (see <see cref="Validate"/>) and supplied via
    /// <c>ASSISTANT_CLOSE_OUT_PROMPT_FILE</c>.
    /// </summary>
    public string CloseOutPrompt { get; init; } = "";

    /// <summary>
    /// Hard cap, in seconds, on the background close-out turn, after which it is abandoned so a
    /// stuck model can't keep a session (and its MCP lease) open forever. Generous by default
    /// because close-out may make real tool calls — loading an MCP server and, say, sending an
    /// email takes several model turns and round-trips. Must be greater than zero.
    /// </summary>
    public int CloseOutTimeoutSeconds { get; init; } = 30;

    /// <summary>Path to the JSON file describing the MCP servers to expose as tools.</summary>
    public string McpConfigFile { get; init; } = "mcpservers.json";

    /// <summary>
    /// Root folder for persistent data. The model's memories are stored as flat <c>.md</c> files
    /// under a <c>memories/</c> sub-folder. Defaults to <c>data</c> (relative to the working
    /// directory).
    /// </summary>
    public string DataDirectory { get; init; } = "data";

    /// <summary>
    /// Debugging aid: when set, the conversation's audio is also written to WAV files in this
    /// directory — the raw stream received from the provider, the 16 kHz stream sent to the
    /// device, and the raw 16 kHz device microphone. Leave empty to disable.
    /// </summary>
    public string? DebugAudioDirectory { get; init; }

    /// <summary>
    /// Experimental mic noise gate. When greater than zero, the device microphone is only
    /// forwarded to the model while the human is talking — its RMS amplitude (PCM16, 0–32767)
    /// must reach this threshold — so the model does not pick up the device's echo of its own
    /// voice. Set to zero to disable the gate and always forward the mic.
    /// </summary>
    public double MicGateThreshold { get; init; } = 1500;

    /// <summary>
    /// How long, in milliseconds, the mic must stay at or above <see cref="MicGateThreshold"/>
    /// before the gate opens. Rejects brief echo transients that would trip a single-packet gate.
    /// Only relevant when the gate is enabled.
    /// </summary>
    public int MicGateAttackMs { get; init; } = 60;

    /// <summary>
    /// Safety backstop, in milliseconds: the gate normally closes when the provider's VAD reports
    /// the user's turn ended, but if that event never arrives it force-closes after this much
    /// continuous quiet. Keep it comfortably longer than the provider's silence window. A negative
    /// value disables the backstop entirely, depending solely on the VAD speech-stopped event.
    /// Only relevant when the gate is enabled.
    /// </summary>
    public int MicGateHoldMs { get; init; } = 4000;

    /// <summary>
    /// How much audio, in milliseconds, before the detected onset to include when the gate opens,
    /// so the quieter run-up to speech is not clipped. Implemented as a look-back buffer of
    /// <see cref="MicGateAttackMs"/> + this many ms. Only relevant when the gate is enabled.
    /// </summary>
    public int MicGatePrerollMs { get; init; } = 80;

    /// <summary>
    /// Validates the configuration at startup, throwing on a fatal misconfiguration so the app
    /// fails fast rather than misbehaving at runtime. <paramref name="enabled"/> is whether an AI
    /// provider is configured (<see cref="IAssistantSessionFactory.IsEnabled"/>); when it is, a
    /// close-out prompt is mandatory: the model is given a final close-out turn at hang-up (which
    /// may finish deferred work such as sending an email), and silently skipping it would be
    /// surprising.
    /// </summary>
    public void Validate(bool enabled)
    {
        if (!enabled)
            return;

        if (string.IsNullOrWhiteSpace(CloseOutPrompt))
            throw new InvalidOperationException(
                "ASSISTANT_CLOSE_OUT_PROMPT_FILE is required when an AI provider is configured: "
                    + "it supplies the prompt for the end-of-conversation close-out turn."
            );

        if (CloseOutTimeoutSeconds <= 0)
            throw new InvalidOperationException(
                "ASSISTANT_CLOSE_OUT_TIMEOUT_SECONDS must be greater than zero."
            );
    }
}
