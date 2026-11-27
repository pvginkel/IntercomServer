using System.Diagnostics;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// Steps of the call state machine that a test builds on. Each asserts every command its step
/// sends the devices it names, the sequences <c>CallRequestTests</c> and <c>CallTests</c> pin.
/// Devices in a call are listed in the order they joined it, the order the server walks them in.
/// </summary>
internal static class Calls
{
    /// <summary>
    /// <paramref name="caller"/> clicks with no call waiting or on, and it and each of
    /// <paramref name="rung"/> are sent the blinking red LED of a call being placed.
    /// </summary>
    public static async Task RequestAsync(TestDevice caller, params TestDevice[] rung)
    {
        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.Calling));
        foreach (var device in rung)
            await device.ExpectCommandsAsync(RedLed(Led.Calling));
    }

    /// <summary>
    /// A call between <paramref name="caller"/> and <paramref name="answerer"/>: the caller's
    /// click rings the answerer and <paramref name="alsoRung"/>, and the answerer's click accepts.
    /// </summary>
    public static async Task StartAsync(
        TestDevice caller,
        TestDevice answerer,
        params TestDevice[] alsoRung
    )
    {
        await RequestAsync(caller, [answerer, .. alsoRung]);

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
        foreach (var device in alsoRung)
            await device.ExpectCommandsAsync(RedLed(Led.Off));
    }

    /// <summary><paramref name="joiner"/> clicks during the call <paramref name="inCall"/> are in.</summary>
    public static async Task JoinAsync(TestDevice joiner, params TestDevice[] inCall)
    {
        await joiner.ClickAsync();

        foreach (var device in inCall)
            await device.ExpectCommandsAsync(AddEndpoint(joiner.Endpoint));
        await joiner.ExpectCommandsAsync(
            [.. inCall.Select(device => AddEndpoint(device.Endpoint)), GreenLed(Led.On), Recording(true)]
        );
    }

    /// <summary>
    /// <paramref name="ender"/>, one of <paramref name="inCall"/>, clicks, and the call ends for
    /// each of them: its green LED and recording go off, and the others' endpoints are removed.
    /// </summary>
    public static async Task EndAsync(TestDevice ender, params TestDevice[] inCall)
    {
        await ender.ClickAsync();

        foreach (var device in inCall)
        {
            await device.ExpectCommandsAsync(
                [
                    GreenLed(Led.Off),
                    Recording(false),
                    .. inCall.Where(other => other != device).Select(other => RemoveEndpoint(other.Endpoint)),
                ]
            );
        }
    }

    /// <summary>
    /// No call is waiting or on, and none of the devices has been sent a command since what the
    /// test took from it: <paramref name="clicker"/>'s click is a new request, and each device's
    /// next command is that request's. <paramref name="others"/> are every device it rings.
    /// </summary>
    public static Task AssertNoCallAsync(TestDevice clicker, params TestDevice[] others) =>
        RequestAsync(clicker, others);

    /// <summary>
    /// Asserts the ring has stopped on each of <paramref name="rung"/> by now: in the second that
    /// follows, nothing arrives later than its first half. A ring that played on would still be
    /// arriving at its end. Takes every packet that has arrived.
    /// </summary>
    public static async Task AssertRingStoppedAsync(params TestDevice[] rung)
    {
        var stopped = Stopwatch.GetTimestamp();

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        foreach (var device in rung)
        {
            var last = device.TakeArrivedPackets().LastOrDefault();
            Assert.True(
                last == null
                    || Stopwatch.GetElapsedTime(stopped, last.ReceivedAt) < TimeSpan.FromSeconds(0.5),
                $"The ring played on to device {device.DeviceId}."
            );
        }
    }
}
