using System.Collections.Concurrent;
using IntercomServer.Utils;
using Serilog;

namespace IntercomServer.AIAssistant;

/// <summary>
/// Manages the set of concurrent AI assistant conversations (one per device). Each device can
/// hold its own conversation independently; conversations are not exclusive with each
/// other or with calls. This class owns only the assistant bridge — device control (LEDs,
/// recording, audio endpoints) lives in <see cref="StateManager"/>. Which AI provider backs
/// the conversations is decided by the injected <see cref="IAssistantSessionFactory"/>.
/// </summary>
internal sealed class ConversationManager(
    AssistantConfiguration configuration,
    IAssistantSessionFactory sessionFactory,
    McpToolRegistry mcp,
    MemoryStore memory,
    AudioSender audioSender,
    UdpAudioServer audioServer,
    ConversationCloser closer
)
{
    private static readonly ILogger Logger = Log.ForContext<ConversationManager>();

    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    /// <summary>Raised after a conversation has fully ended (from any cause).</summary>
    public event EventHandler<Device>? SessionEnded;

    /// <summary>
    /// Starts a conversation with <paramref name="device"/>. Returns false when the
    /// feature is not configured, the device cannot be used, or it is already chatting.
    /// </summary>
    public async Task<bool> StartAsync(Device device)
    {
        if (!sessionFactory.IsEnabled)
        {
            Logger.Warning("Assistant conversation requested but no AI provider is configured.");
            return false;
        }

        if (device.Configuration?.Endpoint == null)
        {
            Logger.Warning(
                "Cannot start assistant conversation: device {Device} has no audio endpoint.",
                device.DeviceId
            );
            return false;
        }

        var conversation = new Conversation(
            device,
            configuration,
            sessionFactory,
            mcp,
            memory,
            audioSender,
            audioServer,
            OnConversationClosing
        );

        if (!_conversations.TryAdd(device.DeviceId, conversation))
        {
            Logger.Warning(
                "Device {Device} is already in an assistant conversation.",
                device.DeviceId
            );
            // The conversation already took its MCP lease in the constructor; dispose it to release.
            conversation.Dispose();
            return false;
        }

        try
        {
            await conversation.StartAsync();
            Logger.Information(
                "Started assistant conversation with device {Device}",
                device.DeviceId
            );
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(
                ex,
                "Failed to start assistant conversation with device {Device}",
                device.DeviceId
            );
            _conversations.TryRemove(device.DeviceId, out _);
            conversation.Dispose();
            return false;
        }
    }

    /// <summary>Ends the conversation for a device, if any (idempotent).</summary>
    public Task EndAsync(Device device)
    {
        if (_conversations.TryGetValue(device.DeviceId, out var conversation))
            conversation.End();

        return Task.CompletedTask;
    }

    public bool IsChatting(Device device) => _conversations.ContainsKey(device.DeviceId);

    // Raised once a conversation's live phase ends (hang-up, model goodbye, error or disconnect).
    // The device is freed immediately via SessionEnded; the conversation itself is handed to the
    // closer, which gives the model a final close-out turn and then disposes it. The conversation
    // keeps its MCP lease until that disposal, so MCP tools stay callable during close-out.
    private void OnConversationClosing(Conversation conversation)
    {
        _conversations.TryRemove(conversation.Device.DeviceId, out _);

        SessionEnded?.Invoke(this, conversation.Device);

        closer.Close(conversation);
    }
}
