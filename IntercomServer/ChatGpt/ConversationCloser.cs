using System.Collections.Concurrent;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// Takes ownership of conversations whose live phase has ended and winds them down off to the
/// side, keeping <see cref="ConversationManager"/> focused on the active conversations. For each
/// handed-over conversation it runs the final audio-free close-out turn (see
/// <see cref="Conversation.CloseOutAsync"/>) and then disposes it. By the time a conversation
/// arrives here its device has already been freed, so this all happens in the background.
/// </summary>
internal sealed class ConversationCloser(ChatGptConfiguration configuration)
{
    // Cap the close-out turn so a stuck model can't keep a session open forever. Configurable
    // (CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS) because close-out may now do real work — loading an MCP
    // server and making tool calls (e.g. sending an email) takes several model turns and round-trips.
    private readonly TimeSpan _closeOutTimeout = TimeSpan.FromSeconds(
        configuration.CloseOutTimeoutSeconds
    );

    private static readonly ILogger Logger = Log.ForContext<ConversationCloser>();

    // The conversations currently being wound down (used to keep their background task rooted).
    private readonly ConcurrentDictionary<Conversation, byte> _closing = new();

    /// <summary>Hands a conversation over to be closed out and disposed in the background.</summary>
    public void Close(Conversation conversation)
    {
        _closing.TryAdd(conversation, 0);
        _ = CloseAsync(conversation);
    }

    private async Task CloseAsync(Conversation conversation)
    {
        Logger.Information(
            "Starting close-out for the conversation with device {Device}.",
            conversation.Device.DeviceId
        );

        try
        {
            using var cts = new CancellationTokenSource(_closeOutTimeout);
            await conversation.CloseOutAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning(
                "Close-out for device {Device} did not finish within {Timeout}s.",
                conversation.Device.DeviceId,
                _closeOutTimeout.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Close-out for device {Device} failed", conversation.Device.DeviceId);
        }
        finally
        {
            conversation.Dispose();
            _closing.TryRemove(conversation, out _);
        }
    }
}
