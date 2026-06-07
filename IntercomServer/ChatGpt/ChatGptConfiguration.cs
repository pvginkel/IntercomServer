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

    /// <summary>Path to the JSON file describing the MCP servers to expose as tools.</summary>
    public string McpConfigFile { get; init; } = "mcpservers.json";

    /// <summary>
    /// Debugging aid: when set, the audio received from OpenAI is also written to WAV files
    /// in this directory — both the raw 24 kHz stream and the 16 kHz stream sent to the
    /// device. Leave empty to disable.
    /// </summary>
    public string? DebugAudioDirectory { get; init; }

    public bool IsEnabled => !string.IsNullOrEmpty(ApiKey);
}
