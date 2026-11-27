namespace IntercomServer;

/// <summary>The data volume.</summary>
internal class DataConfiguration
{
    /// <summary>
    /// The folder the server keeps its data in (<c>DATA_DIR</c>): the ChatGPT bridge's memories,
    /// and the app releases under <c>updates/</c>.
    /// </summary>
    public string Directory { get; init; } = "data";
}
