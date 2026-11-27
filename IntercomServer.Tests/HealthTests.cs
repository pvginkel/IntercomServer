using System.Net;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The probes Kubernetes asks: <c>/healthz</c>, liveness, answers whatever the MQTT connection
/// does, and <c>/readyz</c>, readiness, answers whether the server is connected to its broker and
/// subscribed. Both answer from the server's start, before it reaches its broker.
/// </summary>
public sealed class HealthTests
{
    [Fact]
    public async Task Liveness_answers_ok()
    {
        await using var harness = await ServerHarness.StartAsync();

        var (status, body) = await harness.GetHealthAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task A_server_that_cannot_reach_its_broker_is_live_and_not_ready()
    {
        // Nothing listens on the port: the server's first connect is refused, and it keeps trying
        // in the background.
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["MQTT_PORT"] = FreePort.Pick().ToString() },
            awaitReady: false
        );

        var liveness = await harness.GetHealthAsync("/healthz");
        var readiness = await harness.GetHealthAsync("/readyz");

        Assert.Equal((HttpStatusCode.OK, "ok"), liveness);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "mqtt: disconnected"), readiness);
    }
}
