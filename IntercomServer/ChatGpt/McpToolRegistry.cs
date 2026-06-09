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
/// them to the OpenAI Realtime session. Tool calls coming back from the model are executed
/// here against the owning MCP server — the servers themselves are never exposed to OpenAI
/// or the public internet.
///
/// Tools are loaded <b>on demand</b> to keep the session's context small: rather than handing
/// the model every tool up front (hundreds, once a few servers are configured), each server is
/// exposed as a single <c>use_&lt;server&gt;</c> selector tool. When the model calls a selector,
/// the owning <see cref="Conversation"/> adds that one server's actual tools to the live session.
/// This trades one extra round-trip per server for a dramatically smaller baseline tool set.
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

    private static readonly BinaryData NoParameters = BinaryData.FromString(
        """{"type":"object","properties":{},"additionalProperties":false}"""
    );

    // Keyed by the namespaced function name (<server>_<tool>); used to execute a model tool call.
    private readonly Dictionary<string, RegisteredTool> _tools = new();

    // Per-server view used for on-demand loading: the selector ("use_<server>") and the server's
    // tools. Populated once at startup; read-only thereafter.
    private readonly List<ServerEntry> _servers = new();
    private readonly Dictionary<string, ServerEntry> _serversByName = new();
    private readonly Dictionary<string, string> _selectorToServer = new();

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<McpClient>> _connections = new();
    private int _activeConversations;

    private sealed record RegisteredTool(
        McpServerConfig Server,
        string ToolName,
        string Description,
        BinaryData Parameters
    );

    private sealed record ServerEntry(
        string Name,
        string SelectorName,
        string SelectorDescription,
        List<RegisteredTool> Tools
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
            "Registered {Tools} MCP tool(s) across {Servers} server(s); exposed as {Selectors} "
                + "on-demand loader(s).",
            _tools.Count,
            servers.Count,
            _servers.Count
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

        var serverTools = new List<RegisteredTool>();
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

            serverTools.Add(registered);

            Logger.Information(
                "Registered MCP tool {Function} ({Server} -> {Tool})",
                functionName,
                server.Name,
                tool.Name
            );
        }

        if (serverTools.Count == 0)
        {
            Logger.Information("MCP server {Server} exposed no tools; no loader added.", server.Name);
            return;
        }

        var selectorName = MakeFunctionName("use", server.Name);
        if (_serversByName.ContainsKey(server.Name) || !_selectorToServer.TryAdd(selectorName, server.Name))
        {
            Logger.Warning(
                "Duplicate MCP server name or selector {Selector}; skipping its loader.",
                selectorName
            );
            return;
        }

        var entry = new ServerEntry(
            server.Name,
            selectorName,
            BuildSelectorDescription(server, serverTools.Count),
            serverTools
        );
        _servers.Add(entry);
        _serversByName[server.Name] = entry;
    }

    // The description the model sees for a server's "use_<server>" loader. A per-server
    // description in the config makes routing far more reliable than the bare name; when it is
    // absent we fall back to the name itself.
    private static string BuildSelectorDescription(McpServerConfig server, int toolCount)
    {
        var what = string.IsNullOrWhiteSpace(server.Description)
            ? $"the '{server.Name}' integration"
            : server.Description!.Trim();

        return $"Load the tools for {what}. You must call this before you can use any "
            + $"'{server.Name}' tools; calling it makes its {toolCount} tool(s) available to call.";
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

    /// <summary>
    /// Builds the one-per-server <c>use_&lt;server&gt;</c> selector tools that are always present in a
    /// session. Calling one loads that server's actual tools (see <see cref="GetServerTools"/>).
    /// </summary>
    public IEnumerable<RealtimeFunctionTool> GetServerSelectorTools()
    {
        foreach (var entry in _servers)
        {
            yield return new RealtimeFunctionTool(entry.SelectorName)
            {
                FunctionDescription = entry.SelectorDescription,
                FunctionParameters = NoParameters,
            };
        }
    }

    /// <summary>
    /// Maps a selector tool name (<c>use_&lt;server&gt;</c>) back to its server, or returns false when
    /// <paramref name="functionName"/> is not a selector.
    /// </summary>
    public bool TryResolveSelector(string functionName, out string serverName)
    {
        if (_selectorToServer.TryGetValue(functionName, out var name))
        {
            serverName = name;
            return true;
        }

        serverName = "";
        return false;
    }

    /// <summary>Builds the function tool definitions for the tools of a single (loaded) server.</summary>
    public IEnumerable<RealtimeFunctionTool> GetServerTools(string serverName)
    {
        if (!_serversByName.TryGetValue(serverName, out var entry))
            yield break;

        foreach (var registered in entry.Tools)
        {
            yield return new RealtimeFunctionTool(
                MakeFunctionName(registered.Server.Name, registered.ToolName)
            )
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
                AdditionalHeaders = ResolveHeaders(server.Name),
            }
        );

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves the HTTP headers for a server from the environment. Auth is not stored in the
    /// config file: for a server named <c>foo-bar</c> the app reads <c>MCP_TOKEN_FOO_BAR</c>
    /// (name uppercased, <c>-</c> → <c>_</c>) and, when set, sends its value verbatim — scheme
    /// included, e.g. <c>Bearer abc…</c> — as the <c>Authorization</c> header. When the variable
    /// is unset the server is left unauthenticated.
    /// </summary>
    private static Dictionary<string, string>? ResolveHeaders(string serverName)
    {
        var envName = "MCP_TOKEN_" + serverName.ToUpperInvariant().Replace('-', '_');
        var token = Environment.GetEnvironmentVariable(envName);

        return string.IsNullOrEmpty(token)
            ? null
            : new Dictionary<string, string> { ["Authorization"] = token };
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

    /// <summary>
    /// Optional human description of what this server is for (e.g. "Google Workspace: Gmail,
    /// Calendar, Drive"). Surfaced to the model on the <c>use_&lt;name&gt;</c> loader so it can decide
    /// when to load the server. Strongly recommended for large servers.
    /// </summary>
    public string? Description { get; set; }
}
