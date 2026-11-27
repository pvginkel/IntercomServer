using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// A long click with no call waiting or on starts a ChatGPT conversation: the server points the
/// device's microphone at its own audio endpoint and turns its green LED and recording on. With
/// ChatGPT off, as it is in the suite, the start fails, and the server takes all of that back.
/// During a call a long click does nothing. A live conversation needs OpenAI, which the suite
/// does not cover.
/// </summary>
public sealed class ConversationStartTests
{
    [Fact]
    public async Task A_long_click_with_no_call_starts_a_conversation_which_is_undone_with_chatgpt_off()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        var other = await harness.AddDeviceAsync("0x0a0002");
        await device.ComeOnlineAsync();
        await other.ComeOnlineAsync();

        await device.LongClickAsync();

        await device.ExpectCommandsAsync(
            AddEndpoint(harness.AudioEndpoint),
            GreenLed(Led.On),
            Recording(true),
            RemoveEndpoint(harness.AudioEndpoint),
            Recording(false),
            GreenLed(Led.Off)
        );

        // A device in a conversation is not rung: the other's call request rings this one.
        await Calls.AssertNoCallAsync(other, device);
    }

    [Fact]
    public async Task A_long_click_during_a_call_does_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var outside = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await outside.ComeOnlineAsync();
        await Calls.StartAsync(caller, answerer, outside);

        await caller.LongClickAsync();
        await outside.LongClickAsync();

        // The next command each one gets is the end's, then the next request's.
        await Calls.EndAsync(answerer, caller, answerer);
        await Calls.AssertNoCallAsync(outside, caller, answerer);
    }
}
