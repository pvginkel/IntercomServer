using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// What the servers a test starts log from <see cref="Capture"/> on, until it is disposed: the
/// events logged in the test's flow, which every task and timer its servers start inherits.
/// </summary>
internal sealed class ServerLog : IDisposable
{
    private static readonly MessageTemplateTextFormatter Formatter = new("{Message:lj}", CultureInfo.InvariantCulture);

    private readonly string _test;
    private readonly ConcurrentQueue<LogEvent> _events = new();

    private ServerLog(string test)
    {
        _test = test;
    }

    public static ServerLog Capture()
    {
        var log = new ServerLog(TestContext.Current.Test!.UniqueID);
        Assert.True(TestLogging.Captured.TryAdd(log._test, log._events), "The test captures its servers' log twice.");
        return log;
    }

    /// <summary>The messages the server's <typeparamref name="T"/> has logged so far, in order, as the test's output shows them.</summary>
    public List<string> Messages<T>() =>
        _events
            .Where(logEvent =>
                logEvent.Properties.TryGetValue("SourceContext", out var source)
                && source is ScalarValue { Value: string name }
                && name == typeof(T).FullName
            )
            .Select(logEvent =>
            {
                using var writer = new StringWriter();
                Formatter.Format(logEvent, writer);
                return writer.ToString();
            })
            .ToList();

    /// <summary>Waits until the server's <typeparamref name="T"/> has logged <paramref name="message"/>.</summary>
    public async Task LoggedAsync<T>(string message)
    {
        var waited = Stopwatch.StartNew();
        while (!Messages<T>().Contains(message))
        {
            if (waited.Elapsed > Timeouts.Observe)
                throw new TimeoutException($"Waited {Timeouts.Observe.TotalSeconds:0} s for {typeof(T).Name} to log '{message}'.");

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    public void Dispose() => TestLogging.Captured.TryRemove(_test, out _);
}
