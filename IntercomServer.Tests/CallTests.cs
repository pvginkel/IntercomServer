using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// A call, from its accept to its end. A click from any device but the caller accepts a ringing
/// call: the ring stops, and the caller and the device join the call. Joining one sends each
/// device in it the endpoint its new peer's configuration advertises, on <c>set/add_endpoint</c>,
/// and the joiner theirs, and turns the joiner's green LED and recording on. During a call, a
/// click from a device outside it joins it, and one from a device in it ends it for everyone,
/// each endpoint removed on <c>set/remove_endpoint</c>. A device removed from the server leaves
/// the call. Under auto-accept, a request puts every device it would ring straight into the call.
/// </summary>
public sealed class CallTests
{
    // A fifth of a second of the ring, one packet each 20 ms.
    private const int Packets = 10;

    [Fact]
    public async Task A_rung_device_accepts_with_a_click_and_a_click_from_the_call_ends_it()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var other = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await other.ComeOnlineAsync();
        await Calls.RequestAsync(caller, answerer, other);
        await answerer.NextPacketsAsync(Packets);

        await answerer.ClickAsync();

        await caller.ExpectCommandsAsync(
            RedLed(Led.Off),
            GreenLed(Led.On),
            Recording(true),
            AddEndpoint(answerer.Endpoint)
        );
        await answerer.ExpectCommandsAsync(
            RedLed(Led.Off),
            AddEndpoint(caller.Endpoint),
            GreenLed(Led.On),
            Recording(true)
        );
        await other.ExpectCommandsAsync(RedLed(Led.Off));
        await Calls.AssertRingStoppedAsync(answerer, other);

        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(
            GreenLed(Led.Off),
            Recording(false),
            RemoveEndpoint(answerer.Endpoint)
        );
        await answerer.ExpectCommandsAsync(
            GreenLed(Led.Off),
            Recording(false),
            RemoveEndpoint(caller.Endpoint)
        );
        await Calls.AssertNoCallAsync(other, caller, answerer);
    }

    [Fact]
    public async Task A_device_that_clicks_during_a_call_joins_it_and_the_end_reaches_all_three()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var joiner = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await joiner.ComeOnlineAsync();
        await Calls.StartAsync(caller, answerer, joiner);

        await joiner.ClickAsync();

        await caller.ExpectCommandsAsync(AddEndpoint(joiner.Endpoint));
        await answerer.ExpectCommandsAsync(AddEndpoint(joiner.Endpoint));
        await joiner.ExpectCommandsAsync(
            AddEndpoint(caller.Endpoint),
            AddEndpoint(answerer.Endpoint),
            GreenLed(Led.On),
            Recording(true)
        );

        await joiner.ClickAsync();

        await caller.ExpectCommandsAsync(
            GreenLed(Led.Off),
            Recording(false),
            RemoveEndpoint(answerer.Endpoint),
            RemoveEndpoint(joiner.Endpoint)
        );
        await answerer.ExpectCommandsAsync(
            GreenLed(Led.Off),
            Recording(false),
            RemoveEndpoint(caller.Endpoint),
            RemoveEndpoint(joiner.Endpoint)
        );
        await joiner.ExpectCommandsAsync(
            GreenLed(Led.Off),
            Recording(false),
            RemoveEndpoint(caller.Endpoint),
            RemoveEndpoint(answerer.Endpoint)
        );
        await Calls.AssertNoCallAsync(caller, answerer, joiner);
    }

    [Fact]
    public async Task A_device_removed_during_a_call_leaves_it_and_the_others_talk_on()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var removed = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await removed.ComeOnlineAsync();
        await Calls.StartAsync(caller, answerer, removed);
        await Calls.JoinAsync(removed, caller, answerer);

        await removed.PublishConfigurationAsync(null);

        // The others drop its endpoint, and the device itself is sent nothing.
        await caller.ExpectCommandsAsync(RemoveEndpoint(removed.Endpoint));
        await answerer.ExpectCommandsAsync(RemoveEndpoint(removed.Endpoint));

        // The call goes on between the other two, and ends without it.
        await Calls.EndAsync(answerer, caller, answerer);
        await removed.ComeOnlineAsync();
        await Calls.AssertNoCallAsync(removed, caller, answerer);
    }

    [Fact]
    public async Task Under_auto_accept_a_click_puts_every_other_online_enabled_device_straight_into_the_call()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var first = await harness.AddDeviceAsync("0x0a0002");
        var second = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await first.ComeOnlineAsync();
        await second.ComeOnlineAsync();
        await harness.PublishAsync("intercom/server/set/auto_accept", "true");

        await caller.ClickAsync();

        // Nothing blinks: the first device accepts at once, and the second joins.
        await caller.ExpectCommandsAsync(
            RedLed(Led.Off),
            GreenLed(Led.On),
            Recording(true),
            AddEndpoint(first.Endpoint),
            AddEndpoint(second.Endpoint)
        );
        await first.ExpectCommandsAsync(
            RedLed(Led.Off),
            AddEndpoint(caller.Endpoint),
            GreenLed(Led.On),
            Recording(true),
            AddEndpoint(second.Endpoint)
        );
        await second.ExpectCommandsAsync(
            RedLed(Led.Off),
            AddEndpoint(caller.Endpoint),
            AddEndpoint(first.Endpoint),
            GreenLed(Led.On),
            Recording(true)
        );

        await Calls.EndAsync(caller, caller, first, second);
    }

    [Fact]
    public async Task Auto_accept_set_back_to_false_rings_again()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();

        await harness.PublishAsync("intercom/server/set/auto_accept", "true");
        await harness.PublishAsync("intercom/server/set/auto_accept", "false");

        await Calls.RequestAsync(caller, called);
    }
}
