namespace IntercomServer.AIAssistant.Gemini;

/// <summary>
/// Configuration for the Gemini (Google Live API) provider. Populated from environment
/// variables in <c>Program.cs</c>. The provider-neutral assistant settings live in
/// <see cref="AssistantConfiguration"/>.
/// </summary>
internal class GeminiConfiguration
{
    /// <summary>Google API key. When empty, the Gemini provider is disabled.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Live model name, e.g. <c>gemini-3.1-flash-live-preview</c>.</summary>
    public string Model { get; init; } = "gemini-3.1-flash-live-preview";

    /// <summary>
    /// Voice name. One of the Gemini prebuilt voices (Puck, Charon, Kore, Fenrir, Aoede,
    /// Leda, Orus, Zephyr, ...).
    /// </summary>
    public string Voice { get; init; } = "Charon";

    public bool IsEnabled => !string.IsNullOrEmpty(ApiKey);
}
