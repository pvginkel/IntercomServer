using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntercomTestWeb;

// The single, centralized JsonSerializerOptions for the whole app (Appendix A of the spec: the
// device contract was previously duplicated across IntercomClient and Device). Used for the MQTT
// wire format AND the browser-facing REST/WebSocket API, so the DTOs keep their native snake_case
// wire shape end to end and the "MQTT contract is the single source of truth" — no shadow state.
public static class IntercomJson
{
    public static readonly JsonSerializerOptions Options =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
}
