namespace IntercomServer.AIAssistant.ChatGpt;

/// <summary>
/// Configuration for the ChatGPT (OpenAI Realtime API) provider. Populated from environment
/// variables in <c>Program.cs</c>. The provider-neutral assistant settings live in
/// <see cref="AssistantConfiguration"/>.
/// </summary>
internal class ChatGptConfiguration
{
    /// <summary>OpenAI API key. When empty, the ChatGPT provider is disabled.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Realtime model name, e.g. <c>gpt-realtime</c> or <c>gpt-realtime-2</c>.</summary>
    public string Model { get; init; } = "gpt-realtime";

    /// <summary>Model used for the <c>web_search</c> tool's Responses API call.</summary>
    public string WebSearchModel { get; init; } = "gpt-5.5";

    /// <summary>
    /// Voice name. One of the OpenAI realtime voices (alloy, ash, ballad, cedar,
    /// coral, echo, marin, sage, shimmer, verse).
    /// </summary>
    public string Voice { get; init; } = "marin";

    public bool IsEnabled => !string.IsNullOrEmpty(ApiKey);
}
