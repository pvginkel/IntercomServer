namespace IntercomServer;

// Kubernetes-style probes:
//   GET /healthz  - liveness:  the process is up and the web host answers.
//   GET /readyz   - readiness: the MQTT broker connection is established and subscribed.
//
// Liveness deliberately does NOT depend on the MQTT connection. Per Kubernetes guidance a
// liveness probe must not flap on a transient dependency outage, otherwise the orchestrator would
// kill the pod while it is already recovering on its own (see the reconnect loop in Server). The
// MQTT state is surfaced through readiness instead, where the orchestrator can mark the pod
// NotReady and alert without restarting it.
internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => Results.Text("ok"));
        endpoints.MapGet(
            "/readyz",
            (Server server) =>
                server.IsConnected
                    ? Results.Text("mqtt: connected")
                    : Results.Text(
                        "mqtt: disconnected",
                        statusCode: StatusCodes.Status503ServiceUnavailable
                    )
        );

        return endpoints;
    }
}
