namespace IntercomServer.AIAssistant;

/// <summary>
/// A provider-neutral update produced by <see cref="IAssistantSession.ReceiveUpdatesAsync"/>.
/// The session translates its provider's wire protocol into this small semantic vocabulary;
/// everything that does not require an action from the consumer (protocol bookkeeping, turn
/// continuation, provider errors) is handled inside the session and never surfaces here.
/// </summary>
internal abstract record AssistantUpdate;

/// <summary>
/// A chunk of the assistant's spoken reply: mono PCM16 at
/// <see cref="IAssistantSession.OutputSampleRate"/>. Delivery is typically faster than real
/// time; the consumer is responsible for pacing it out to the device.
/// </summary>
internal sealed record OutputAudioUpdate(byte[] Audio) : AssistantUpdate;

/// <summary>
/// The current reply's audio is complete. The consumer should release any audio it is still
/// holding back (e.g. a jitter pre-roll), so a reply shorter than the pre-roll still plays.
/// </summary>
internal sealed record OutputAudioEndedUpdate : AssistantUpdate;

/// <summary>
/// The provider detected that the user started speaking (barge-in). The session itself
/// interrupts the in-flight reply; the consumer should drop any output audio it has buffered
/// so playback stops promptly instead of talking over the user.
/// </summary>
internal sealed record UserSpeechStartedUpdate : AssistantUpdate;

/// <summary>
/// The provider's voice activity detection decided the user's turn ended. The signal may be
/// explicit (OpenAI's speech-stopped event) or inferred (Gemini: the model starting to speak),
/// so consumers should treat it as a strong hint rather than ground truth — the mic gate
/// honors it only when the mic has actually gone quiet, and keeps its time-based backstop.
/// </summary>
internal sealed record UserSpeechEndedUpdate : AssistantUpdate;

/// <summary>
/// The assistant called a function tool. The consumer must execute it and report the outcome
/// via <see cref="IAssistantSession.SubmitToolResultAsync"/>; the session takes care of any
/// turn continuation the provider needs after that. Tools the provider implements natively
/// (e.g. web search) are handled inside the session and never surface as this update.
/// </summary>
internal sealed record ToolCallUpdate(string CallId, string Name, string ArgumentsJson)
    : AssistantUpdate;
