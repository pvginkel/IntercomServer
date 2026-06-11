namespace IntercomServer.AIAssistant.ChatGpt;

/// <summary>Creates <see cref="ChatGptSession"/>s — the OpenAI Realtime API provider.</summary>
internal sealed class ChatGptSessionFactory(
    ChatGptConfiguration configuration,
    WebSearchTool webSearch
) : IAssistantSessionFactory
{
    public bool IsEnabled => configuration.IsEnabled;

    public IAssistantSession CreateSession() => new ChatGptSession(configuration, webSearch);
}
