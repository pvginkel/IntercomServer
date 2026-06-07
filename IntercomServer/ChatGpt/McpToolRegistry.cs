// RealtimeFunctionTool comes from the experimental OpenAI Realtime API (OPENAI002).
#pragma warning disable OPENAI002

using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI.Realtime;
using Serilog;

namespace IntercomServer.ChatGpt;

/// <summary>
/// Connects to the configured remote (HTTP) MCP servers as a client, discovers their
/// tools, and exposes them to the OpenAI Realtime session as ordinary function tools.
/// Tool calls coming back from the model are routed to the owning MCP server and executed
/// here — the MCP servers themselves are never exposed to OpenAI or the public internet.
///
/// To add a new MCP server, drop an entry in the JSON config file (see docs/CHATGPT_MCP.md).
/// No code changes are required.
/// </summary>
internal sealed class McpToolRegistry(ChatGptConfiguration configuration)
{
    private static readonly ILogger Logger = Log.ForContext<McpToolRegistry>();

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, RegisteredTool> _tools = new();
    private readonly List<McpClient> _clients = [];

    private record RegisteredTool(McpClient Client, McpClientTool Tool);

    public async Task InitializeAsync()
    {
        var path = configuration.McpConfigFile;

        if (!File.Exists(path))
        {
            Logger.Information(
                "No MCP configuration file at {Path}; ChatGPT will run without MCP tools.",
                path
            );
            return;
        }

        McpServersConfig? config;
        try
        {
            await using var stream = File.OpenRead(path);
            config = await JsonSerializer.DeserializeAsync<McpServersConfig>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read MCP configuration file {Path}", path);
            return;
        }

        foreach (var server in config?.Servers ?? [])
        {
            try
            {
                await ConnectServerAsync(server);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to connect to MCP server {Server}", server.Name);
            }
        }

        Logger.Information(
            "Registered {Tools} MCP tool(s) across {Servers} server(s).",
            _tools.Count,
            _clients.Count
        );
    }

    private async Task ConnectServerAsync(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Url))
        {
            Logger.Warning("Skipping MCP server entry with a missing name or url.");
            return;
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                AdditionalHeaders = server.Headers,
            }
        );

        var mcpClient = await McpClient.CreateAsync(transport);
        _clients.Add(mcpClient);

        foreach (var tool in await mcpClient.ListToolsAsync())
        {
            var functionName = MakeFunctionName(server.Name, tool.Name);

            if (!_tools.TryAdd(functionName, new RegisteredTool(mcpClient, tool)))
            {
                Logger.Warning("Duplicate tool name {Function}; skipping.", functionName);
                continue;
            }

            Logger.Information(
                "Registered MCP tool {Function} ({Server} -> {Tool})",
                functionName,
                server.Name,
                tool.Name
            );
        }
    }

    /// <summary>Builds the OpenAI function tool definitions for every registered MCP tool.</summary>
    public IEnumerable<RealtimeFunctionTool> GetRealtimeTools()
    {
        foreach (var (functionName, registered) in _tools)
        {
            yield return new RealtimeFunctionTool(functionName)
            {
                FunctionDescription = registered.Tool.Description,
                FunctionParameters = BinaryData.FromString(registered.Tool.JsonSchema.GetRawText()),
            };
        }
    }

    /// <summary>Invokes a previously registered MCP tool and returns its textual output.</summary>
    public async Task<string> CallAsync(
        string functionName,
        string argumentsJson,
        CancellationToken cancellationToken
    )
    {
        if (!_tools.TryGetValue(functionName, out var registered))
            return $"Error: unknown tool '{functionName}'.";

        Dictionary<string, object?>? arguments = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                argumentsJson,
                JsonOptions
            );
        }

        var result = await registered.Client.CallToolAsync(
            registered.Tool.Name,
            arguments,
            cancellationToken: cancellationToken
        );

        var text = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock textBlock)
                text.AppendLine(textBlock.Text);
        }

        var output = text.ToString().Trim();
        if (output.Length == 0)
            output = result.IsError == true ? "The tool reported an error." : "(no output)";

        return output;
    }

    private static string MakeFunctionName(string server, string tool)
    {
        var sanitized = new string(
            $"{server}_{tool}"
                .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')
                .ToArray()
        );

        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }
}

internal sealed class McpServersConfig
{
    public List<McpServerConfig> Servers { get; set; } = [];
}

internal sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
}
