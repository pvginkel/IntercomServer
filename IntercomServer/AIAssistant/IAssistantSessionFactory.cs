namespace IntercomServer.AIAssistant;

/// <summary>
/// Creates the <see cref="IAssistantSession"/>s that back conversations. The registered
/// implementation decides which AI provider the intercom talks to; see
/// <c>ChatGpt.ChatGptSessionFactory</c> for the OpenAI one.
/// </summary>
internal interface IAssistantSessionFactory
{
    /// <summary>Whether the provider is configured (e.g. its API key is set).</summary>
    bool IsEnabled { get; }

    /// <summary>Creates a new, unconnected session. Must not block or throw.</summary>
    IAssistantSession CreateSession();
}
