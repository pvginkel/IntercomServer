// The OpenAI .NET Responses API is shipped as an experimental (evaluation) surface and
// raises OPENAI001. We knowingly depend on it; suppress the diagnostic for this file.
#pragma warning disable OPENAI001

using System.Text.Json;
using OpenAI.Responses;
using Serilog;

namespace IntercomServer.AIAssistant.ChatGpt;

/// <summary>
/// Backs the <c>web_search</c> function tool of the ChatGPT provider. When the model calls
/// it, this runs a separate OpenAI Responses API request (a configurable model, default
/// gpt-5.5) with the hosted web-search tool enabled, and returns the answer text to be fed
/// back into the conversation. Handled inside <see cref="ChatGptSession"/>; other providers
/// bring their own web search (e.g. Gemini's native Google Search grounding).
/// </summary>
internal sealed class WebSearchTool(ChatGptConfiguration configuration)
{
    public const string ToolName = "web_search";

    private static readonly ILogger Logger = Log.ForContext<WebSearchTool>();

    public static AssistantTool GetTool() =>
        new(
            ToolName,
            "Search the web for current or factual information and return a concise answer. "
                + "Use this for recent events, or anything you are unsure about.",
            BinaryData.FromString(
                """{"type":"object","properties":{"query":{"type":"string","description":"What to search the web for."}},"required":["query"],"additionalProperties":false}"""
            )
        );

    public async Task<string> SearchAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var query = ExtractQuery(argumentsJson);
        if (string.IsNullOrWhiteSpace(query))
            return "No search query was provided.";

        Logger.Information("Running web search ({Model}) for {Query}", configuration.WebSearchModel, query);

        var client = new ResponsesClient(configuration.ApiKey!);

        var options = new CreateResponseOptions(
            configuration.WebSearchModel,
            [ResponseItem.CreateUserMessageItem(query)]
        )
        {
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Low,
                ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Concise,
            },
        };

        options.Tools.Add(ResponseTool.CreateWebSearchTool());

        var response = await client.CreateResponseAsync(options, cancellationToken);

        var text = response.Value.GetOutputText();
        return string.IsNullOrWhiteSpace(text) ? "No answer was found." : text;
    }

    private static string ExtractQuery(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson
            );

            return document.RootElement.TryGetProperty("query", out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
