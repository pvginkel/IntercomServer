using System.Diagnostics;
using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// A call nobody answers rings for 15 seconds, and is then cancelled as the caller's second
/// click cancels it. It is a class of its own so that its wait runs beside the other tests.
/// </summary>
public sealed class UnansweredCallTests
{
    [Fact]
    public async Task A_call_nobody_answers_is_cancelled_after_15_seconds()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();

        await caller.ClickAsync();

        // The server sets its alarm after it sends the caller this, so the alarm's 15 seconds
        // start no earlier than this command arrives, give or take its delivery.
        await caller.ExpectCommandsAsync(RedLed(Led.Calling));
        var requested = Stopwatch.GetTimestamp();
        await called.ExpectCommandsAsync(RedLed(Led.Calling));

        await caller.ExpectCommandsAsync(RedLed(Led.Off));
        var rang = Stopwatch.GetElapsedTime(requested);
        await called.ExpectCommandsAsync(RedLed(Led.Off));
        await Calls.AssertRingStoppedAsync(called);

        Assert.InRange(rang, TimeSpan.FromSeconds(14.5), TimeSpan.FromSeconds(17.5));
        await Calls.AssertNoCallAsync(caller, called);
    }
}
