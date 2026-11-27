namespace IntercomServer.Tests.Harness;

/// <summary>
/// How long a test waits for what it expects to see. The suite gates the production deploy and
/// runs on a loaded, shared Jenkins agent, so a wait is generous: it bounds a test that is
/// already red, and costs a green one nothing.
/// </summary>
internal static class Timeouts
{
    /// <summary>The longest a test waits for one thing it observes: a message, a packet, a start.</summary>
    public static readonly TimeSpan Observe = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for the next item of <paramref name="reader"/>, and fails the test, naming
    /// <paramref name="what"/>, when none comes within <see cref="Observe"/>.
    /// </summary>
    public static async Task<T> NextAsync<T>(
        System.Threading.Channels.ChannelReader<T> reader,
        string what
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        timeout.CancelAfter(Observe);

        try
        {
            return await reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Waited {Observe.TotalSeconds:0} s for {what}, and none came.");
        }
    }
}
