# Change discipline

The rules every code change in this repo obeys. This is the doc `.aiworkflowrc` names as
`design_philosophy`: it is handed to every `code-writer`, `code-reviewer`, `plan-writer` and
`plan-reviewer` the pipeline dispatches, and a reviewer cites it when sending work back. It states
rules; what the server does is in its code and in [`CHATGPT_MCP.md`](CHATGPT_MCP.md), and the
architecture model in [`architecture/architecture.yaml`](architecture/architecture.yaml).

## The wire contracts belong to the devices too

Nothing outside this repo imports its code, but three surfaces are read by systems this repo does
not own:

- **MQTT**: the `intercom/client/<id>/…` topics the devices publish and the `…/set/…` topics the
  server publishes back (`Server.cs`, `Device.cs`), and `intercom/server/set/…`, which Home
  Assistant publishes. Topic names, payload shapes, casing and enum strings are shared verbatim
  with the device firmware. The JSON shapes are the DTOs in `IntercomServer.Utils`.
- **UDP audio**: PCM mono, 16 kHz, 16-bit, in 20 ms chunks, each datagram a 4-byte big-endian packet
  index and its payload (`AudioSender.cs`, `Constants.cs`), paced in real time because the devices'
  jitter buffers are small (`AudioStreaming.cs`).
- **The deploy**: every environment variable, prompt file and secret name the server reads is set by
  pvginkel/IntercomDeploy's chart. A new or renamed one changes the chart in the same slice, or the
  pod crash-loops on the next roll: `ChatGptConfiguration.Validate` refuses a missing close-out
  prompt at startup.

Inside this repo, **fix the callers**: no shim, adapter or second code path that keeps an old shape
alive. A change to MQTT or UDP that the firmware must follow is a breaking change to a consumer
nobody here can update in the same commit: make it only where the slice's design says so, and name
it in the close-out so the operator can schedule the firmware side. The firmware is never waited on,
watched or ordered from here.

## Devices by their id

The server addresses a device by its MQTT device id, the `<id>` of its topics, never by its IP
address. An IP is where a device happens to be today; an id is what the operator, Home Assistant and
the firmware all name it by.

## No tombstones

Delete replaced code completely. No "moved to X" comments, forwarding stubs, deprecated aliases,
commented-out blocks or unused settings and embedded resources. Superseded prose is rewritten, not
annotated; git history records what things used to be.

## No defensive coding

No `catch` that swallows an error, no drop-the-bad-input-and-keep-going path, no null-guard for a
condition the code already prevents, no silent fallback for configuration the server requires.
Configuration is checked once, at startup, and a bad value stops the process
(`ChatGptConfiguration.Validate` is the pattern).

**Boundary validation is the exception, and it is the point.** Validate what crosses in from
outside: MQTT payloads, UDP datagrams, environment variables, files on the data volume, OpenAI's and
the MCP servers' answers. A device that goes offline or sends an unknown action is an expected
condition: the MQTT handler logs it and carries on with the next message, as `Server.cs` does,
rather than taking down the connection.

## Testability is critical

**Every change ships with its tests, and the suite pins behaviour before it changes.** A change to
the playback core, device discovery, packets, pacing, loop and cancel, the doorbell's reach or the
call state machine comes after a test that pins today's behaviour of what it touches.

**Real systems, no fakes**: an MQTTnet broker started in the test process, a localhost UDP
listener, a temporary data directory, the real FFmpeg, and tokens signed by a key the tests make.
The ChatGPT bridge's conversation with OpenAI is the one part not covered.

The suite does not exist yet. Until it does, `kc project test` has no statement to run, and what
proves a change is the build, the lint and the test phase's check of the production rollout
([`slice-test-plan.md`](slice-test-plan.md)). The slice that adds the suite also adds its `test:`
verb and the Jenkins test stage.

## Build the project, never the solution

Every build names `IntercomServer/IntercomServer.csproj`. The solution also carries IntercomTest, a
Windows WPF harness that cannot build on Linux, so a solution-wide build is red by construction.
`.kubecoder/project.yaml` says why at length. IntercomTest is the operator's manual tool, not a
suite: a change keeps `IntercomServer.Utils`, which it shares, compiling for it, and nothing here
verifies that.

## The architecture model is hand-authored

`docs/architecture/architecture.yaml` is written here, by hand, and collected centrally. A change
that adds or removes an external edge (an interface, a dependency, an MQTT namespace) updates it in
the same slice, keeping the existing ids stable; `kc project lint` validates it against the
federation.

## Code conventions

- **Logging**: Serilog, a `private static readonly ILogger Logger = Log.ForContext<T>();` per class,
  structured placeholders (`{Device}`), messages as English sentences.
- **Configuration**: environment variables parsed in `Program.cs` onto plain `*Configuration`
  classes with `init` properties; no `IConfiguration` or `IOptions`. A default lives in one place.
- **Hosting**: Microsoft.Extensions.Hosting, singletons injected through primary constructors,
  `internal` types, and hosted services started in registration order (the health endpoint first).
- **Async**: `async`/`await` with a `CancellationToken` through every long-running path.
- **Comments**: XML doc comments on configuration classes and public types; inline comments say why,
  never what.

## This is a public repo

IntercomServer is public on GitHub. No secrets, hostnames, IP addresses or other facts of the
operator's network go in code, tests, docs or commit messages. They live in the private
pvginkel/IntercomDeploy, and a secret's value lives in OpenBao, set by the operator, never by a
slice.
