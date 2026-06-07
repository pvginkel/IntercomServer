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
/// Discovers the tools exposed by the configured remote (HTTP/SSE) MCP servers and exposes
/// them to the OpenAI Realtime session as ordinary function tools. Tool calls coming back
/// from the model are executed here against the owning MCP server — the servers themselves
/// are never exposed to OpenAI or the public internet.
///
/// Tools are discovered once at startup (each discovery connection is closed immediately).
/// Execution connections are opened lazily and kept alive only while at least one
/// conversation is active (reference-counted via <see cref="BeginConversation"/> /
/// <see cref="EndConversation"/>), then closed. This avoids a long-lived idle SSE session,
/// which servers and proxies drop (a later call would otherwise fail with "No active SSE
/// connection for session …"), while still sharing one connection across overlapping
/// conversations.
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

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<McpClient>> _connections = new();
    private int _activeConversations;

    private sealed record RegisteredTool(
        McpServerConfig Server,
        string ToolName,
        string Description,
        BinaryData Parameters
    );

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

        var servers = config?.Servers ?? [];
        foreach (var server in servers)
        {
            try
            {
                await DiscoverServerAsync(server);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to connect to MCP server {Server}", server.Name);
            }
        }

        Logger.Information(
            "Registered {Tools} MCP tool(s) across {Servers} server(s).",
            _tools.Count,
            servers.Count
        );
    }

    /// <summary>Discovers a server's tools over a short-lived connection, then closes it.</summary>
    private async Task DiscoverServerAsync(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Url))
        {
            Logger.Warning("Skipping MCP server entry with a missing name or url.");
            return;
        }

        await using var client = await ConnectAsync(server, CancellationToken.None);

        foreach (var tool in await client.ListToolsAsync())
        {
            var functionName = MakeFunctionName(server.Name, tool.Name);

            var registered = new RegisteredTool(
                server,
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.JsonSchema.GetRawText())
            );

            if (!_tools.TryAdd(functionName, registered))
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

    /// <summary>
    /// Marks a conversation as active. While at least one is active, execution connections
    /// are kept open and shared. Balance every call with <see cref="EndConversation"/>.
    /// </summary>
    public void BeginConversation()
    {
        lock (_gate)
        {
            _activeConversations++;
        }
    }

    /// <summary>
    /// Marks a conversation as ended. When the last one ends, all open execution connections
    /// are closed.
    /// </summary>
    public void EndConversation()
    {
        Task<McpClient>[]? toDispose = null;

        lock (_gate)
        {
            if (_activeConversations > 0)
                _activeConversations--;

            if (_activeConversations == 0 && _connections.Count > 0)
            {
                toDispose = [.. _connections.Values];
                _connections.Clear();
            }
        }

        if (toDispose != null)
            _ = DisposeAsync(toDispose);
    }

    /// <summary>Builds the OpenAI function tool definitions for every registered MCP tool.</summary>
    public IEnumerable<RealtimeFunctionTool> GetRealtimeTools()
    {
        foreach (var (functionName, registered) in _tools)
        {
            yield return new RealtimeFunctionTool(functionName)
            {
                FunctionDescription = registered.Description,
                FunctionParameters = registered.Parameters,
            };
        }
    }

    /// <summary>Invokes a registered MCP tool and returns its textual output.</summary>
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

        var client = await GetConnectionAsync(registered.Server);

        var result = await client.CallToolAsync(
            registered.ToolName,
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

    /// <summary>Returns the shared connection for a server, opening it on first use.</summary>
    private Task<McpClient> GetConnectionAsync(McpServerConfig server)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(server.Name, out var existing))
                return existing;

            // Not tied to a single conversation's token: the connection is shared.
            var task = ConnectTrackedAsync(server);
            _connections[server.Name] = task;
            return task;
        }
    }

    private async Task<McpClient> ConnectTrackedAsync(McpServerConfig server)
    {
        try
        {
            return await ConnectAsync(server, CancellationToken.None);
        }
        catch
        {
            // Don't leave a faulted connection cached; a later call should retry.
            lock (_gate)
            {
                _connections.Remove(server.Name);
            }
            throw;
        }
    }

    private static async Task<McpClient> ConnectAsync(
        McpServerConfig server,
        CancellationToken cancellationToken
    )
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                AdditionalHeaders = server.Headers,
            }
        );

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static async Task DisposeAsync(IEnumerable<Task<McpClient>> connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                var client = await connection;
                await client.DisposeAsync();
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
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
