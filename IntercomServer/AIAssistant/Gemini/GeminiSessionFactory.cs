namespace IntercomServer.AIAssistant.Gemini;

/// <summary>Creates <see cref="GeminiSession"/>s — the Google Live API provider.</summary>
internal sealed class GeminiSessionFactory(GeminiConfiguration configuration)
    : IAssistantSessionFactory
{
    public bool IsEnabled => configuration.IsEnabled;

    public IAssistantSession CreateSession() => new GeminiSession(configuration);
}
