# ChatGPT voice assistant & MCP tools

This intercom can hold a spoken conversation with ChatGPT using the
[OpenAI Realtime API](https://developers.openai.com/api/docs/guides/voice-agents),
and you can give that assistant extra abilities by plugging in **MCP servers**.

## How a user starts a conversation

- **Long‑press** the button on a device that is idle (not in a call, not ringing,
  not being dialed) → the device starts a ChatGPT voice conversation.
- **Short‑press** that same device's button, or simply say *"goodbye"*, ends it.
- Conversations are **per device and concurrent**: several devices can talk to ChatGPT
  at the same time, and other devices can still call each other. The only restriction is
  that a device which is in a conversation is **not rung** by incoming calls until it
  hangs up (the doorbell still sounds on it).

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
| `CHATGPT_WEB_SEARCH_MODEL` | no | `gpt-5.5` | Model used by the built-in `web_search` tool (see below). |
| `CHATGPT_INSTRUCTIONS` | no | a built‑in persona | System prompt / persona for the assistant (inline). |
| `CHATGPT_INSTRUCTIONS_FILE` | no | — | Path to a file containing the system prompt. Takes precedence over `CHATGPT_INSTRUCTIONS`; read at startup. May contain the `{NOW}` placeholder (see below). |
| `CHATGPT_LOCALE` | no | host culture | Culture used to format substituted values such as `{NOW}` (e.g. `nl-NL`). |
| `MCP_CONFIG_FILE` | no | `mcpservers.json` | Path to the MCP server list (see below). |
| `CHATGPT_DEBUG_AUDIO_DIR` | no | — | Debugging only: when set, the audio received from OpenAI is also written to WAV files in this directory (the raw 24 kHz stream and the 16 kHz stream sent to the device). |

The system prompt may include the placeholder **`{NOW}`**, which is replaced at the start of
each conversation with the current date and time as a natural, culture-specific long
date + time (with `CHATGPT_LOCALE=nl-NL`, a Dutch-formatted date like *"… 7 juni 2026 15:45"*).
It is substituted per conversation, so the time is always current rather than frozen at startup.

The server's UDP audio endpoint is a general (non‑ChatGPT) setting:

| Variable | Required | Default | Meaning |
| --- | --- | --- | --- |
| `AUDIO_PORT` | no | `5004` | UDP port the server listens on for inbound device audio. |
| `AUDIO_HOST` | no | auto‑detected LAN IPv4 | The address devices stream their audio to. **Must be reachable by the devices.** Behind NAT or a Kubernetes LoadBalancer set this to the external/LB IP — auto‑detection returns this host's own NIC address, which inside a Kubernetes pod is the (unreachable) pod IP. |

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
- A built‑in `web_search` tool is always available: when the model calls it, the server
  runs a separate OpenAI Responses API request (`CHATGPT_WEB_SEARCH_MODEL`, default
  `gpt-5.5`) with the hosted web‑search tool, low reasoning effort and a concise reasoning
  summary, and feeds the answer back into the conversation.
