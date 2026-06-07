using System.Collections.Concurrent;
using IntercomServer.Utils;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// Manages the set of concurrent ChatGPT conversations (one per device). Each device can
/// hold its own conversation independently; conversations are not exclusive with each
/// other or with calls. This class owns only the OpenAI bridge — device control (LEDs,
/// recording, audio endpoints) lives in <see cref="StateManager"/>.
/// </summary>
internal sealed class ConversationManager(
    ChatGptConfiguration configuration,
    McpToolRegistry mcp,
    AudioSender audioSender,
    UdpAudioServer audioServer
)
{
    private static readonly ILogger Logger = Log.ForContext<ConversationManager>();

    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    private string? _audioEndpoint;

    /// <summary>Raised after a conversation has fully ended (from any cause).</summary>
    public event EventHandler<Device>? SessionEnded;

    /// <summary>
    /// The audio endpoint (host:port) that a device should stream its microphone to while
    /// it is in a conversation. This is the address devices must be able to reach.
    /// </summary>
    public string AudioEndpoint =>
        _audioEndpoint ??= $"{ResolveAdvertisedHost()}:{audioServer.LocalEndPoint.Port}";

    /// <summary>
    /// Starts a conversation with <paramref name="device"/>. Returns false when the
    /// feature is not configured, the device cannot be used, or it is already chatting.
    /// </summary>
    public async Task<bool> StartAsync(Device device)
    {
        if (!configuration.IsEnabled)
        {
            Logger.Warning("ChatGPT conversation requested but no OpenAI API key is configured.");
            return false;
        }

        if (device.Configuration?.Endpoint == null)
        {
            Logger.Warning(
                "Cannot start ChatGPT conversation: device {Device} has no audio endpoint.",
                device.DeviceId
            );
            return false;
        }

        var conversation = new Conversation(
            device,
            configuration,
            mcp,
            audioSender,
            audioServer,
            OnConversationEnded
        );

        if (!_conversations.TryAdd(device.DeviceId, conversation))
        {
            Logger.Warning("Device {Device} is already in a ChatGPT conversation.", device.DeviceId);
            return false;
        }

        try
        {
            await conversation.StartAsync();
            Logger.Information("Started ChatGPT conversation with device {Device}", device.DeviceId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start ChatGPT conversation with device {Device}", device.DeviceId);
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

    private void OnConversationEnded(Conversation conversation)
    {
        _conversations.TryRemove(conversation.Device.DeviceId, out _);

        SessionEnded?.Invoke(this, conversation.Device);
    }

    private string ResolveAdvertisedHost()
    {
        if (!string.IsNullOrEmpty(configuration.AdvertisedHost))
            return configuration.AdvertisedHost;

        var address = NetworkUtils.GetNetworkIPAddresses().FirstOrDefault();
        if (address == null)
        {
            throw new InvalidOperationException(
                "Could not auto-detect a LAN IP address for the audio endpoint. "
                    + "Set the CHATGPT_AUDIO_HOST environment variable."
            );
        }

        return address.ToString();
    }
}
