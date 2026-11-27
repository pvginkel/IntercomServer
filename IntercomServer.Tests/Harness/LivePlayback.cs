using System.Net;
using IntercomServer.ChatGpt.Audio;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The path a ChatGPT reply takes to a device, with the test in OpenAI's place: the ChatGPT
/// bridge's <see cref="LiveAudioStream"/>, played by <see cref="AudioStreaming.PlayAsync"/>
/// through the server's own <see cref="AudioSender"/>, as <c>Conversation</c> plays it. It is
/// the one source the server has that blocks, so it is how a test shows a late chunk or a stall.
/// The stream has no pre-roll, so audio leaves as soon as the test appends it.
/// </summary>
internal sealed class LivePlayback : IAsyncDisposable
{
    /// <summary>One 20 ms chunk at mono 16 kHz 16-bit, which every device in production plays.</summary>
    public const int ChunkBytes = 640;

    public static readonly TimeSpan ChunkLength = TimeSpan.FromMilliseconds(20);

    private readonly LiveAudioStream _stream = new(prerollBytes: 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _playing;

    private LivePlayback(AudioSender sender, IPEndPoint endpoint)
    {
        _playing = AudioStreaming.PlayAsync(
            sender,
            [endpoint],
            _stream,
            configuration: null,
            _stopping.Token
        );
    }

    /// <summary>Starts playing to <paramref name="device"/>'s endpoint; nothing goes out until audio is appended.</summary>
    public static LivePlayback Start(ServerHarness harness, TestDevice device) =>
        new(harness.Services.GetRequiredService<AudioSender>(), device.AudioEndpoint);

    /// <summary>Appends <paramref name="chunks"/> chunks of silence at once, as a burst of OpenAI's audio arrives.</summary>
    public void Append(int chunks) => _stream.Append(new byte[chunks * ChunkBytes]);

    /// <summary>Ends the stream, and returns once everything appended has been sent.</summary>
    public async Task CompleteAsync()
    {
        _stream.Complete();
        await _playing;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _playing;
        }
        catch (OperationCanceledException) { }
        _stopping.Dispose();
    }
}
