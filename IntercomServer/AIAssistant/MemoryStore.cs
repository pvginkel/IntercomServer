using System.Text.Json;
using Serilog;

namespace IntercomServer.AIAssistant;

/// <summary>
/// A simple file-backed memory the model can manage during conversations. Each memory is a
/// flat Markdown file named by its slug (which must end in <c>.md</c>) in the configured
/// folder. The first line of the file is its title, used as the summary in
/// <c>list_memories</c> and the <c>{MEMORIES}</c> prompt placeholder.
///
/// Exposes four function tools: <c>list_memories</c>, <c>get_memory</c>, <c>put_memory</c>
/// and <c>delete_memory</c>. Get/put/delete are by slug. The memories live in a
/// <c>memories/</c> sub-folder of the configured data directory.
/// </summary>
internal sealed class MemoryStore(AssistantConfiguration configuration)
{
    public const string ListTool = "list_memories";
    public const string GetTool = "get_memory";
    public const string PutTool = "put_memory";
    public const string DeleteTool = "delete_memory";

    private static readonly ILogger Logger = Log.ForContext<MemoryStore>();

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Folder holding the memory files: a <c>memories/</c> sub-folder of the data directory.</summary>
    private string MemoryDirectory => Path.Combine(configuration.DataDirectory, "memories");

    public bool Handles(string toolName) =>
        toolName is ListTool or GetTool or PutTool or DeleteTool;

    public IEnumerable<AssistantTool> GetTools()
    {
        yield return Tool(
            ListTool,
            "List all stored memories. Returns a Markdown list of '[summary](slug)'.",
            """{"type":"object","properties":{},"additionalProperties":false}"""
        );

        yield return Tool(
            GetTool,
            "Read the full Markdown content of a memory by its slug (e.g. groceries.md).",
            """{"type":"object","properties":{"slug":{"type":"string","description":"The memory's file name, ending in .md."}},"required":["slug"],"additionalProperties":false}"""
        );

        yield return Tool(
            PutTool,
            "Create or overwrite a memory. The slug must end in .md; the first line of the "
                + "content must be a short title that is used as the memory's summary.",
            """{"type":"object","properties":{"slug":{"type":"string","description":"The memory's file name, ending in .md."},"content":{"type":"string","description":"The full Markdown content; the first line must be a title."}},"required":["slug","content"],"additionalProperties":false}"""
        );

        yield return Tool(
            DeleteTool,
            "Delete a memory by its slug.",
            """{"type":"object","properties":{"slug":{"type":"string","description":"The memory's file name, ending in .md."}},"required":["slug"],"additionalProperties":false}"""
        );
    }

    private static AssistantTool Tool(string name, string description, string parameters) =>
        new(name, description, BinaryData.FromString(parameters));

    public async Task<string> CallAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken
    )
    {
        var args = ParseArguments(argumentsJson);

        return toolName switch
        {
            ListTool => ListMemories(),
            GetTool => await GetMemoryAsync(Arg(args, "slug"), cancellationToken),
            PutTool => await PutMemoryAsync(Arg(args, "slug"), Arg(args, "content"), cancellationToken),
            DeleteTool => DeleteMemory(Arg(args, "slug")),
            _ => $"Unknown memory tool '{toolName}'.",
        };
    }

    /// <summary>Renders the memory list used for the <c>{MEMORIES}</c> prompt placeholder.</summary>
    public string RenderMemoriesList()
    {
        if (!Directory.Exists(MemoryDirectory))
            return "";

        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(MemoryDirectory, "*.md").OrderBy(f => f))
        {
            string? firstLine;
            try
            {
                firstLine = File.ReadLines(file).FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not read memory {File}", file);
                continue;
            }

            var slug = Path.GetFileName(file);
            var title = TitleOf(firstLine);
            lines.Add($"- [{(title.Length > 0 ? title : slug)}]({slug})");
        }

        return string.Join("\n", lines);
    }

    private string ListMemories()
    {
        var list = RenderMemoriesList();
        return list.Length > 0 ? list : "There are no memories yet.";
    }

    private async Task<string> GetMemoryAsync(string? slug, CancellationToken cancellationToken)
    {
        if (!TryResolve(slug, out var path, out var error))
            return error;
        if (!File.Exists(path))
            return $"There is no memory named '{slug}'.";

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private async Task<string> PutMemoryAsync(
        string? slug,
        string? content,
        CancellationToken cancellationToken
    )
    {
        if (!TryResolve(slug, out var path, out var error))
            return error;
        if (string.IsNullOrWhiteSpace(content))
            return "The memory content is empty.";
        if (TitleOf(FirstLine(content)).Length == 0)
            return "The first line of the memory must be a title.";

        Directory.CreateDirectory(MemoryDirectory);
        await File.WriteAllTextAsync(path, content, cancellationToken);

        Logger.Information("Saved memory {Slug}", slug);
        return $"Saved memory '{slug}'.";
    }

    private string DeleteMemory(string? slug)
    {
        if (!TryResolve(slug, out var path, out var error))
            return error;
        if (!File.Exists(path))
            return $"There is no memory named '{slug}'.";

        File.Delete(path);
        Logger.Information("Deleted memory {Slug}", slug);
        return $"Deleted memory '{slug}'.";
    }

    // Validates the slug and resolves it to a path inside the memory folder, rejecting
    // anything that is not a simple "<name>.md" file (no directory traversal).
    private bool TryResolve(string? slug, out string path, out string error)
    {
        path = "";
        error = "";

        if (string.IsNullOrWhiteSpace(slug))
        {
            error = "A slug is required.";
            return false;
        }

        if (!slug.EndsWith(".md", StringComparison.Ordinal))
        {
            error = "The slug must end in .md.";
            return false;
        }

        if (
            slug.Contains('/')
            || slug.Contains('\\')
            || slug.Contains("..")
            || Path.GetFileName(slug) != slug
        )
        {
            error = "The slug must be a plain file name, e.g. groceries.md.";
            return false;
        }

        path = Path.Combine(MemoryDirectory, slug);
        return true;
    }

    private static Dictionary<string, JsonElement> ParseArguments(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                    JsonOptions
                ) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string? Arg(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstLine(string content) => content.Split('\n', 2)[0];

    private static string TitleOf(string? line) => (line ?? "").TrimStart('#').Trim();
}
