# ChatGPT voice assistant & MCP tools

This intercom can hold a spoken conversation with ChatGPT using the
[OpenAI Realtime API](https://developers.openai.com/api/docs/guides/voice-agents),
and you can give that assistant extra abilities by plugging in **MCP servers**.

## How a user starts a conversation

- **Long‑press** the button on a device that is idle (not in a call, not ringing,
  not being dialed) → the device starts a ChatGPT voice conversation.
- **Short‑press** that same device's button, or simply say *"goodbye"*, ends it.
- A conversation is **exclusive**, exactly like a call: while one device is talking
  to ChatGPT the rest of the system is busy, and you cannot start a call and a
  conversation at the same time.

Under the hood the server tells the device to stream its microphone (over UDP) to
an audio endpoint the server exposes, bridges that audio to OpenAI, and streams the
model's spoken reply back to the device over the same path used for ring tones.
Audio is resampled between the intercom's 16 kHz and OpenAI's 24 kHz automatically.

## Enabling the feature

The feature is **off** until an OpenAI API key is present. Configuration is read
from environment variables (same style as the existing `MQTT_*` settings):

| Variable | Required | Default | Meaning |
| --- | --- | --- | --- |
| `OPENAI_API_KEY` | **yes** | — | OpenAI API key. When empty the whole feature is disabled. |
| `CHATGPT_MODEL` | no | `gpt-realtime` | Realtime model name (e.g. `gpt-realtime`, `gpt-realtime-2`). |
| `CHATGPT_VOICE` | no | `marin` | Voice: `alloy`, `ash`, `ballad`, `cedar`, `coral`, `echo`, `marin`, `sage`, `shimmer`, `verse`. |
| `CHATGPT_INSTRUCTIONS` | no | a built‑in persona | System prompt / persona for the assistant. |
| `CHATGPT_AUDIO_PORT` | no | `5004` | UDP port the server listens on for the device microphone stream. |
| `CHATGPT_AUDIO_HOST` | no | auto‑detected LAN IPv4 | The address devices stream their microphone to. **Must be reachable by the devices.** Set this explicitly when the server is multi‑homed or running behind NAT / a Kubernetes Service (e.g. a NodePort address). |
| `MCP_CONFIG_FILE` | no | `mcpservers.json` | Path to the MCP server list (see below). |

## Plugging in an MCP server

> **The app is the MCP client.** It connects to *your* MCP servers privately over
> HTTP and re‑exposes their tools to ChatGPT as ordinary function calls. Your MCP
> servers are **never** handed to OpenAI and are **never** exposed to the public
> internet — when the model wants a tool, OpenAI asks *this server*, and this
> server calls your MCP server locally and returns the result.

Adding an MCP server is a **config change only — no code**:

1. Create a file called `mcpservers.json` next to the server binary (or point
   `MCP_CONFIG_FILE` at it). See [`mcpservers.example.json`](../mcpservers.example.json).

   ```json
   {
     "servers": [
       {
         "name": "home",
         "url": "http://homeassistant.local:8123/mcp/",
         "headers": { "Authorization": "Bearer REPLACE_WITH_TOKEN" }
       },
       {
         "name": "notes",
         "url": "http://10.0.0.5:9000/mcp"
       }
     ]
   }
   ```

   | Field | Required | Meaning |
   | --- | --- | --- |
   | `name` | yes | A short label, unique per server. Used to namespace its tools. |
   | `url` | yes | The MCP server's HTTP endpoint (Streamable HTTP or SSE — auto‑detected). |
   | `headers` | no | Extra HTTP headers sent on every request, e.g. an `Authorization` bearer token. |

2. Restart the server. On startup the log shows what was discovered:

   ```
   Registered MCP tool home_toggle_light (home -> toggle_light)
   Registered 7 MCP tool(s) across 2 server(s).
   ```

3. That's it. The model can now call those tools during a conversation. Each tool
   is exposed to the model as `{name}_{tool}` (sanitised to letters/digits/`_`/`-`,
   truncated to 64 characters) so two servers can expose tools with the same name
   without colliding.

### Notes & limitations

- **Transport:** remote **HTTP** MCP servers (Streamable HTTP / SSE). The official
  [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk)
  C# SDK is used as the client. (stdio/subprocess servers are not wired up; expose
  them over HTTP if you need them.)
- **Approvals:** tool calls run automatically (no approval prompt) — appropriate
  for tools you already trust and run yourself.
- **Secrets:** `mcpservers.json` may contain tokens, so it is git‑ignored. Commit
  `mcpservers.example.json` instead.
- A built‑in `end_conversation` tool is always available to the model so it can
  hang up when you say goodbye.
