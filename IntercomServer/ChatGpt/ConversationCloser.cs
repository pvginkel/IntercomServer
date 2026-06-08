using System.Collections.Concurrent;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// Takes ownership of conversations whose live phase has ended and winds them down off to the
/// side, keeping <see cref="ConversationManager"/> focused on the active conversations. For each
/// handed-over conversation it gives the model one last audio-free turn to persist memory (see
/// <see cref="Conversation.FlushMemoryAsync"/>) and then disposes it. By the time a conversation
/// arrives here its device has already been freed, so this all happens in the background.
/// </summary>
internal sealed class ConversationCloser
{
    // Cap the closing memory-flush turn so a stuck model can't keep a session open forever.
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(15);

    private static readonly ILogger Logger = Log.ForContext<ConversationCloser>();

    // The conversations currently being wound down (used to keep their background task rooted).
    private readonly ConcurrentDictionary<Conversation, byte> _closing = new();

    /// <summary>Hands a conversation over to be flushed and disposed in the background.</summary>
    public void Close(Conversation conversation)
    {
        _closing.TryAdd(conversation, 0);
        _ = CloseAsync(conversation);
    }

    private async Task CloseAsync(Conversation conversation)
    {
        try
        {
            using var cts = new CancellationTokenSource(FlushTimeout);
            await conversation.FlushMemoryAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning(
                "Memory flush for device {Device} did not finish within {Timeout}s.",
                conversation.Device.DeviceId,
                FlushTimeout.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Memory flush for device {Device} failed", conversation.Device.DeviceId);
        }
        finally
        {
            conversation.Dispose();
            _closing.TryRemove(conversation, out _);
        }
    }
}
