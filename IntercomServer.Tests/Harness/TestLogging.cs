using System.Collections.Concurrent;
using IntercomServer.Tests.Harness;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

[assembly: AssemblyFixture(typeof(TestLogging))]

namespace IntercomServer.Tests.Harness;

/// <summary>
/// Sends the server's log to the output of the test that started it, so a red test shows what
/// its server did. The server's classes each take a logger from <see cref="Log.Logger"/> once,
/// when the class is first used, so it is set here, before any test runs.
/// </summary>
public sealed class TestLogging
{
    /// <summary>
    /// By the unique id of a test that captures its servers' log (<see cref="ServerLog"/>), the
    /// events logged in its flow since.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, ConcurrentQueue<LogEvent>> Captured = new();

    public TestLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new TestOutputSink())
            .CreateLogger();
    }

    // TestContext.Current follows the test's async flow, which every task and timer the server
    // starts inherits. An event after its test ended finds no output helper, or one that throws;
    // Serilog keeps a sink's exception away from the code that logged.
    private sealed class TestOutputSink : ILogEventSink
    {
        private readonly MessageTemplateTextFormatter _formatter =
            new("{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        public void Emit(LogEvent logEvent)
        {
            if (TestContext.Current.Test is { } test && Captured.TryGetValue(test.UniqueID, out var captured))
                captured.Enqueue(logEvent);

            var output = TestContext.Current.TestOutputHelper;
            if (output == null)
                return;

            using var writer = new StringWriter();
            _formatter.Format(logEvent, writer);
            output.Write(writer.ToString());
        }
    }
}
