namespace IntercomServer.AIAssistant;

/// <summary>
/// Bound when <c>ASSISTANT_VOICE_PROVIDER</c> is not set: the AI assistant feature is off and
/// conversation requests are refused (see <see cref="ConversationManager.StartAsync"/>).
/// </summary>
internal sealed class DisabledSessionFactory : IAssistantSessionFactory
{
    public bool IsEnabled => false;

    public IAssistantSession CreateSession() =>
        throw new InvalidOperationException("No AI provider is configured.");
}
