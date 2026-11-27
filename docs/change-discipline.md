# Change discipline

The rules every code change in this repo obeys. This is the doc `.aiworkflowrc` names as
`design_philosophy`: it is handed to every `code-writer`, `code-reviewer`, `plan-writer` and
`plan-reviewer` the pipeline dispatches, and a reviewer cites it when sending work back. It states
rules; what the server does is in its code, in [`devices.md`](devices.md),
[`web-host.md`](web-host.md) and [`CHATGPT_MCP.md`](CHATGPT_MCP.md), and the architecture model in
[`architecture/architecture.yaml`](architecture/architecture.yaml).

## The wire contracts belong to the devices too

Nothing outside this repo imports its code, but four surfaces are read by systems this repo does
not own:

- **MQTT**: the `<prefix>/<id>/…` topics the devices publish under each prefix in
  `DEVICES_TOPIC_PREFIXES` (`intercom/client` and `toilet_automation` by default), the `…/set/…`
  topics the server publishes back under the device's own prefix (`Server.cs`, `Device.cs`), and
  `intercom/server/set/…`, which Home Assistant publishes. Topic names, payload shapes, casing and
  enum strings are shared verbatim with the intercom and ToiletAutomation firmware. The JSON shapes
  are the DTOs in `IntercomServer.Utils`.
- **UDP audio**: PCM in the format each device advertises as `audio_formats.out` (`Device.cs`), in
  20 ms chunks split past 1468 bytes, each datagram a 4-byte big-endian packet index from the
  device's own counter and its payload (`AudioSender.cs`), paced in real time because the devices'
  jitter buffers are small (`AudioStreaming.cs`). [`devices.md`](devices.md) has the details.
- **HTTP**: the web host's routes, which the app and Jenkins call: `/api/ambient-tracks` and
  `/api/app-releases` behind a Keycloak bearer token and a client role (`TrackEndpoints.cs`,
  `AppReleaseEndpoints.cs`, `SignIn.cs`), and `/updates`, MyDownloadsServer's two-call update
  contract, with none. Paths, form fields and their order, JSON shapes and status codes are shared
  verbatim with them. [`web-host.md`](web-host.md) has the details.
- **The deploy**: pvginkel/IntercomDeploy's chart sets the environment variables, prompt files and
  secret names production runs with. A setting the server has no default for, new or renamed,
  changes the chart in the same slice, or the pod crash-loops on the next roll:
  `ChatGptConfiguration.Validate` refuses a missing close-out prompt at startup. A setting whose
  default is in code, such as `HTTP_PORT` or `DEVICES_TOPIC_PREFIXES`, changes the chart only
  when production needs another value.
  IntercomDeploy's PreSync hook applies its Terraform, the Keycloak clients among it, from whatever
  reaches its `main`, so every commit there, a README's included, reaches `main` only at a SHA a
  plan Job of stage `prd` has read on a topic branch. A slice's change there, its README included,
  is in the code phases' commits, which the test phase pushes through the plan Job
  ([`slice-test-plan.md`](slice-test-plan.md), step 2), and nothing is committed there after that
  push. Jenkins' image pins, and the revert of one, change nothing the hook applies and reach
  `main` with no plan Job.

Inside this repo, **fix the callers**: no shim, adapter or second code path that keeps an old shape
alive. A change to MQTT or UDP that the firmware must follow, or to the HTTP API that the app must
follow, is a breaking change to a consumer nobody here can update in the same commit: make it only
where the slice's design says so, and name it in the close-out so the operator can schedule the
other side. The firmware is never waited on, watched or ordered from here.

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
outside: MQTT payloads, UDP datagrams, HTTP requests, environment variables, files on the data
volume, OpenAI's and the MCP servers' answers. A device that goes offline or sends an unknown action
is an expected condition: the MQTT handler logs it and carries on with the next message, as
`Server.cs` does, rather than taking down the connection.

## Testability is critical

**Every change ships with its tests, and the suite pins behaviour before it changes.** A change to
the playback core, device discovery, packets, pacing, loop and cancel, the doorbell's reach or the
call state machine comes after a test that pins today's behaviour of what it touches.

**Real systems, no fakes**: an MQTTnet broker started in the test process, a localhost UDP
listener, a temporary data directory, the real FFmpeg, and tokens signed by a key the tests make.
The ChatGPT bridge's conversation with OpenAI is the one part not covered.

**The suite** is `IntercomServer.Tests`, on xUnit v3. `kc project test` runs it in the `dotnet`
sidecar, with the FFmpeg `kc project setup` installs there, and the Jenkins `Test` stage installs
FFmpeg and runs it before the image is built, so a red suite never deploys. A test starts the real
server, composed as production composes it, with
`await using var harness = await ServerHarness.StartAsync(settings)`: a broker, a Keycloak realm
(`ServerHarness.Issuer`, which signs the tests' tokens: `Issuer.TokenWithRoles("user")`), ports and
data directory of its own, and only the settings the harness gives it, which `settings` adds to or
overrides by their environment variable names, so ChatGPT is off. It drives the server as the
devices, Home Assistant, the app and Jenkins do, through the parts in
`IntercomServer.Tests/Harness/`: HTTP through `ServerHarness.SendAsync`, on the port the web host
bound. Test classes run in parallel, and a timing pin joins `PacingTests`, a collection that runs
alone. Nothing marks a message handled, so a check that something was *not* sent stands on a later
positive observation: a control device the same sound reached, or
`ServerHarness.AssertSentNoCommandAsync`.

## Build the project, never the solution

Every build names a project: `IntercomServer/IntercomServer.csproj`, or the suite's
`IntercomServer.Tests/IntercomServer.Tests.csproj`, which references it. The solution also carries
IntercomTest, a Windows WPF harness that cannot build on Linux, so a solution-wide build is red by
construction. `.kubecoder/project.yaml` says why at length. IntercomTest is the operator's manual
tool, not a suite: a change keeps `IntercomServer.Utils`, which it shares, compiling for it, and
nothing here verifies that.

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
- **Hosting**: Microsoft.Extensions.Hosting with ASP.NET Core's web host inside it, singletons
  injected through primary constructors, `internal` types, and hosted services started in
  registration order (the web host first, so the probes answer from the start). The web host reads
  no configuration source: Kestrel's port and every framework option are set in code.
- **Async**: `async`/`await` with a `CancellationToken` through every long-running path.
- **Comments**: XML doc comments on configuration classes and public types; inline comments say why,
  never what.

## This is a public repo

IntercomServer is public on GitHub. No secrets, hostnames, IP addresses or other facts of the
operator's network go in code, tests, docs or commit messages. They live in the private
pvginkel/IntercomDeploy, and a secret's value lives in OpenBao, set by the operator, never by a
slice.
