using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IntercomServer;

// Minimal HTTP server exposing Kubernetes-style probes:
//   GET /healthz  - liveness:  the process is up and the listener is responsive.
//   GET /readyz   - readiness: the MQTT broker connection is established and subscribed.
//
// Liveness deliberately does NOT depend on the MQTT connection. Per Kubernetes guidance a
// liveness probe must not flap on a transient dependency outage, otherwise the orchestrator would
// kill the pod while it is already recovering on its own (see the reconnect loop in Server). The
// MQTT state is surfaced through readiness instead, where the orchestrator can mark the pod
// NotReady and alert without restarting it.
internal class HealthEndpointService(HealthEndpointConfiguration configuration, Server server)
    : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<HealthEndpointService>();

    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://+:{configuration.Port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token), CancellationToken.None);

        Logger.Information(
            "Health endpoint listening on port {Port} (/healthz, /readyz)",
            configuration.Port
        );

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener.Close();

        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Health endpoint loop ended with an error");
            }
        }
    }

    private async Task AcceptLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to accept a health probe request");
                continue;
            }

            try
            {
                await HandleRequest(context);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to handle a health probe request");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        var (status, body) = path switch
        {
            "/healthz" => (HttpStatusCode.OK, "ok"),
            "/readyz" => server.IsConnected
                ? (HttpStatusCode.OK, "mqtt: connected")
                : (HttpStatusCode.ServiceUnavailable, "mqtt: disconnected"),
            _ => (HttpStatusCode.NotFound, "not found"),
        };

        var payload = Encoding.UTF8.GetBytes(body);

        var response = context.Response;
        response.StatusCode = (int)status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = payload.Length;

        await response.OutputStream.WriteAsync(payload, 0, payload.Length);
        response.Close();
    }
}
