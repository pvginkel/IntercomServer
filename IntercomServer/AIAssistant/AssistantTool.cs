namespace IntercomServer.AIAssistant;

/// <summary>
/// A provider-neutral function tool definition: a name, a description for the model, and a
/// JSON Schema (as raw JSON) describing the parameters. Each <see cref="IAssistantSession"/>
/// implementation converts these to its provider's native tool representation.
/// </summary>
internal sealed record AssistantTool(string Name, string Description, BinaryData ParametersJson);
