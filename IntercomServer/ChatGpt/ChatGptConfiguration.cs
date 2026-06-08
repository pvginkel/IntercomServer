namespace IntercomServer.ChatGpt;

/// <summary>
/// Configuration for the ChatGPT (OpenAI Realtime API) integration. Populated
/// from environment variables in <c>Program.cs</c>, mirroring how
/// <see cref="ServerConfiguration"/> is loaded.
/// </summary>
internal class ChatGptConfiguration
{
    /// <summary>OpenAI API key. When empty, the ChatGPT feature is disabled.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Realtime model name, e.g. <c>gpt-realtime</c> or <c>gpt-realtime-2</c>.</summary>
    public string Model { get; init; } = "gpt-realtime";

    /// <summary>Model used for the <c>web_search</c> tool's Responses API call.</summary>
    public string WebSearchModel { get; init; } = "gpt-5.5";

    /// <summary>
    /// Culture used to format dynamic values substituted into the instructions, such as the
    /// <c>{NOW}</c> placeholder (e.g. <c>nl-NL</c>). When empty, the host's current culture.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Voice name. One of the OpenAI realtime voices (alloy, ash, ballad, cedar,
    /// coral, echo, marin, sage, shimmer, verse).
    /// </summary>
    public string Voice { get; init; } = "marin";

    /// <summary>System instructions / persona handed to the model on session start.</summary>
    public string Instructions { get; init; } =
        "You are a helpful, friendly voice assistant built into a home intercom. "
        + "Keep your answers short and conversational, suitable for being spoken aloud. "
        + "When the user says goodbye or clearly wants to stop, call the end_conversation tool to hang up.";

    /// <summary>
    /// Instruction handed to the model on the final, audio-free turn when a conversation ends, so
    /// it can persist anything worth remembering using its memory tools before hang-up.
    /// </summary>
    public string MemoryFlushPrompt { get; init; } =
        "The conversation has ended. Silently review what was said and, using your memory tools, "
        + "save or update anything worth remembering for next time. Focus specifically on "
        + "preferences and corrections the user stated, and store information that will answer the "
        + "users question faster next time. Produce no spoken reply.";

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
    /// directory — the raw 24 kHz stream received from OpenAI, the 16 kHz stream sent to the
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
    /// Safety backstop, in milliseconds: the gate normally closes when the server's VAD reports
    /// the user's turn ended, but if that event never arrives it force-closes after this much
    /// continuous quiet. Keep it comfortably longer than the server's silence window. A negative
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

    public bool IsEnabled => !string.IsNullOrEmpty(ApiKey);
}
