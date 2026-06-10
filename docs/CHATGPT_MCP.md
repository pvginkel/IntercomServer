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
| `CHATGPT_INSTRUCTIONS_FILE` | no | — | Path to a file containing the system prompt; read at startup. When unset, a built‑in persona is used. May contain the `{NOW}` and `{MEMORIES}` placeholders (see below). |
| `CHATGPT_CLOSE_OUT_PROMPT_FILE` | **yes** (when enabled) | — | Path to a file containing the free‑form instruction for the end‑of‑conversation **close‑out** turn (see *Close‑out turn* below); read at startup. **Required** whenever `OPENAI_API_KEY` is set — there is no built‑in default, and the app refuses to start without it. |
| `CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS` | no | `30` | Hard cap on the background close‑out turn (see *Close‑out turn* below). Generous by default because close‑out may make MCP tool calls (e.g. sending an email). Must be greater than zero. |
| `CHATGPT_LOCALE` | no | host culture | Culture used to format substituted values such as `{NOW}` (e.g. `nl-NL`). |
| `MCP_CONFIG_FILE` | no | `mcpservers.json` | Path to the MCP server list (see below). |
| `DATA_DIR` | no | `data` | Root folder for the server's persistent data. The model's memories live in a `memories/` sub-folder (see *Memory* below). |
| `CHATGPT_DEBUG_AUDIO_DIR` | no | — | Debugging only: when set, the audio received from OpenAI is also written to WAV files in this directory (the raw 24 kHz stream and the 16 kHz stream sent to the device). |

The system prompt may include the placeholder **`{NOW}`**, which is replaced at the start of
each conversation with the current date and time as a natural, culture-specific long
date + time (with `CHATGPT_LOCALE=nl-NL`, a Dutch-formatted date like *"… 7 juni 2026 15:45"*).
It is substituted per conversation, so the time is always current rather than frozen at startup.

The prompt may also include **`{MEMORIES}`**, replaced (per conversation) with a Markdown
list of the stored memories — see *Memory* below. With no memories yet it becomes empty.

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
         "description": "Home Assistant: control lights, climate, scenes and read sensors."
       },
       {
         "name": "google",
         "url": "http://10.0.0.5:9000/mcp",
         "description": "Google Workspace: Gmail, Calendar, Contacts, Drive and Tasks."
       }
     ]
   }
   ```

   | Field | Required | Meaning |
   | --- | --- | --- |
   | `name` | yes | A short label, unique per server. Used to namespace its tools **and** as the join key to its auth token (see below). |
   | `url` | yes | The MCP server's HTTP endpoint (Streamable HTTP or SSE — auto‑detected). |
   | `description` | no | What the server is for. Shown to the model on the server's `use_<name>` loader (alongside the server's tool names) so it knows when to load it (see *On‑demand tool loading* below). **Strongly recommended for large servers** — with it absent the model has only the `name` and the bare tool names to go on. |

   **Authentication is supplied from the environment, not this file.** For each server,
   the app looks up an environment variable named `MCP_TOKEN_<NAME>`, where `<NAME>` is the
   server's `name` uppercased with `-` turned into `_` (so `trello` → `MCP_TOKEN_TRELLO`,
   `home-assistant` → `MCP_TOKEN_HOME_ASSISTANT`). When that variable is set, its value is sent
   **verbatim** as the `Authorization` header on every request; when it is unset, the server is
   left unauthenticated. The value is the *full* header including the scheme — e.g.
   `Bearer abc…` or `Basic abc…` — so the scheme lives in the secret store, not in code. Because
   `name` is the join key, keep server names stable.

2. Restart the server. On startup the log shows what was discovered:

   ```
   Registered MCP tool home_toggle_light (home -> toggle_light)
   Registered 134 MCP tool(s) across 5 server(s); exposed as 5 on-demand loader(s).
   ```

3. That's it. The model can now call those tools during a conversation. Each tool
   is exposed to the model as `{name}_{tool}` (sanitised to letters/digits/`_`/`-`,
   truncated to 64 characters) so two servers can expose tools with the same name
   without colliding.

### On‑demand tool loading

Tool **definitions** (name, description, JSON schema) cost context tokens on every turn, and a
realtime voice session re‑pays that cost continually. With hundreds of tools across several
servers that baseline becomes large, so MCP tools are **not** handed to the model up front.

Instead, each server is exposed as a single **`use_<name>`** loader tool (so `home` →
`use_home`). The loader's description carries the server's `description` **and the names of all the
tools it exposes** (names only — not their schemas, which is the cost on‑demand loading exists to
defer), so the model can route on the concrete capabilities. When it decides it needs that server
it calls the loader. The server then adds *that one server's* actual tools to the live session
(via a realtime `session.update`), and the model calls them on its next turn. Only the always‑on
tools (`end_conversation`, `web_search`, the memory tools) and the per‑server loaders are present
at the start of a conversation; a server's real tools enter the session only once it is loaded,
and stay loaded for the rest of that conversation.

The trade‑off is **one extra round‑trip** the first time each server is used — noticeable as a
short pause in a spoken conversation — in exchange for a much smaller, cheaper baseline. A
per‑server **`description`** still matters: together with the listed tool names it is what the
model sees when choosing what to load.

### Notes & limitations

- **Transport:** remote **HTTP** MCP servers (Streamable HTTP / SSE). The official
  [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk)
  C# SDK is used as the client. (stdio/subprocess servers are not wired up; expose
  them over HTTP if you need them.)
- **Approvals:** tool calls run automatically (no approval prompt) — appropriate
  for tools you already trust and run yourself.
- **Secrets:** `mcpservers.json` holds no credentials — tokens come from the
  `MCP_TOKEN_<NAME>` environment variables instead. The file is still git‑ignored as
  deployment‑specific config; commit `mcpservers.example.json` instead.
- A built‑in `end_conversation` tool is always available to the model so it can
  hang up when you say goodbye.
- A built‑in `web_search` tool is always available: when the model calls it, the server
  runs a separate OpenAI Responses API request (`CHATGPT_WEB_SEARCH_MODEL`, default
  `gpt-5.5`) with the hosted web‑search tool, low reasoning effort and a concise reasoning
  summary, and feeds the answer back into the conversation.

## Memory

The model can remember things across conversations. Memories are stored in a `memories/`
sub-folder of `DATA_DIR` (default `data/memories`) — a flat list of Markdown files, no
subfolders, each named by its **slug**, which must end in `.md`. The **first line** of the
file is its title, used as the summary. Four function tools are exposed to the model:

| Tool | Arguments | Behaviour |
| --- | --- | --- |
| `list_memories` | — | Returns a Markdown list of `- [summary](slug)` for every memory. |
| `get_memory` | `slug` | Returns the full Markdown content of that memory. |
| `put_memory` | `slug`, `content` | Creates or overwrites the memory. Validated: the slug must end in `.md` and be a plain file name (no paths), and the first line of `content` must be a non‑empty title. |
| `delete_memory` | `slug` | Deletes that memory. |

The system prompt placeholder **`{MEMORIES}`** is replaced at the start of each conversation
with the current `list_memories` output, e.g.:

```
- [Groceries to buy](groceries.md)
- [Wifi password](wifi.md)
```

So the model sees what it has remembered, and can `get_memory` by slug to read the details.
The folder is plain `.md` files, so you can read and edit them yourself.

### Close-out turn

When a conversation ends, the model is given one final turn. As soon as the conversation
closes — the user hangs up, or the model says goodbye — the device is freed immediately and, in
the background, the model is handed a final **text-only** turn driven by the *close-out prompt*.
The prompt is free-form and the turn keeps the **same tools the live session had** (memory, web
search and any MCP servers already loaded via their `use_<server>` loaders), so the close-out turn
can actually *finish work* the user asked for on their way out:

> *"Send an email to Alice saying I'll be ten minutes late — thanks, bye."* → the user hangs up →
> in the background the model loads the Google server (if it hadn't already), drafts the mail and
> sends it, then persists anything worth remembering — all without holding the line open.

This is possible because each conversation holds an **MCP lease** that keeps its server
connections open until the conversation is fully disposed, i.e. *after* the close-out turn — not
at hang-up. The close-out turn **reuses the existing session tools** rather than sending its own
list, so it adds no tool-schema tokens and is purely text-only (no audio is generated for the
freed device). `end_conversation` stays available but simply reports that the call has already
ended. (A conversation that drops because of a network error skips close-out, since there is no
session left to ask.)

The turn is capped so a stuck model can't keep a session open forever — 30 seconds by default,
configurable with `CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS`.

The close-out prompt is **required** when the feature is enabled — there is no built-in default,
and the app refuses to start without `CHATGPT_CLOSE_OUT_PROMPT_FILE`. A typical prompt asks the
model to carry out any deferred request and then save anything worth remembering for next time.
