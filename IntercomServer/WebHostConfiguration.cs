namespace IntercomServer;

/// <summary>The web host's settings.</summary>
internal class WebHostConfiguration
{
    /// <summary>The TCP port the web host listens on, on every address (<c>HTTP_PORT</c>).</summary>
    public int Port { get; init; } = 8080;
}
