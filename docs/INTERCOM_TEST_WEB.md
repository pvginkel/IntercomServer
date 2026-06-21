# IntercomTest Web — Architecture

Status: Proposed · Last updated: 2026-06-21

A web-based replacement for the WPF **IntercomTest** desktop app, used to develop,
test and debug intercom devices. This document records the target architecture and
**all technical choices** so the port can be executed (and maintained) without
re-litigating decisions.

---

## 1. Purpose & scope

Reproduce 100% of the current IntercomTest feature set as a browser app backed by a
small C# service. No new features. This is a **testing tool**, run only from a dev
environment, accessed over the LAN by hostname (HTTPS).

Features to preserve (all of them):

- Monitor & control **real** devices over MQTT (state/LEDs/volume/enabled, identify,
  restart, remove, audio-config editor).
- Run one or more **simulated** intercom devices on the PC (live mic capture +
  speaker playback, button click / long-click).
- **AEC test** tool: record a sample, play it to a device, capture the loopback,
  and visualize it live as a waveform or FFT spectrogram.
- Server-level commands: ring doorbell, auto-accept toggle.
- Background **audio recorder** (dumps incoming device audio to timestamped WAVs).

### Non-goals

- No deployment/hosting story. Dev environment only.
- No multi-user concerns, auth, or persistence beyond what the desktop app had.
- The ChatGPT / Realtime / MCP conversation logic stays in `IntercomServer` and is
  **out of scope** — IntercomTest never contained it (it only triggers it via a
  device `long_click`).

---

## 2. The two planes (why the architecture looks the way it does)

IntercomTest speaks to devices over two independent transports:

| Plane | Transport | Carries | Browser-capable? |
|-------|-----------|---------|------------------|
| **Control** | MQTT | discovery, state, LED/volume/enable, button actions, doorbell, audio-config | Only via MQTT-over-WS |
| **Audio** | Raw UDP | 16 kHz/16-bit/mono PCM, 4-byte sequence header | **No — browsers can't do UDP** |

The audio plane is the entire reason a browser-only app is impossible: a browser
cannot send or receive raw UDP to LAN devices. Therefore a backend process that
*can* speak UDP is mandatory, and the browser is a thin control + audio-bridge +
visualization surface in front of it.

---

## 3. Chosen architecture (overview)

A single **ASP.NET Core (net9.0)** process that is essentially "headless
IntercomTest": it keeps all MQTT and UDP audio handling in C# (reusing the existing
`IntercomServer.Utils` library and most of IntercomTest's non-UI code), and exposes
a small HTTP + WebSocket API. A **React** single-page app, served as static files by
the same process, is the UI.

```
                          Browser (React SPA, HTTPS)
   ┌─────────────────────────────────────────────────────────────────┐
   │  Control UI   │  AEC view + <canvas>  │  Simulated-device audio │
   └───────┬───────────────┬─────────────────────────────┬───────────┘
           │ REST          │ WS (JSON)                   │ WS (binary, PCM)
           │ commands      │ state + spectrogram cols    │ mic up / speaker down
   ┌───────▼───────────────▼─────────────────────────────▼───────────┐
   │             ASP.NET Core backend  (one process, Kestrel)        │
   │  Command API · State hub · Audio bridge · Spectrogram producer  │
   │  ────────────────────────────────────────────────────────────── │
   │  Reused C#: MQTT clients · UdpAudioServer · AudioMixer ·        │
   │             FFT/window/normalize · WAV I/O · DTO contract       │
   └───────┬────────────────────────────────────────────┬────────────┘
           │ MQTT (control)                             │ UDP (audio)
   ┌───────▼────────────┐                      ┌────────▼───────────┐
   │   MQTT broker      │                      │ Real / sim devices │
   └────────────────────┘                      └────────────────────┘
```

---

## 4. Technical decisions

Every binding choice, with rationale. Decisions marked **(directed)** were specified
by the project owner.

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| D1 | Backend platform | **ASP.NET Core, net9.0**, minimal API, Kestrel | Reuses `IntercomServer.Utils` and ~all of IntercomTest's non-UI C# (UDP, mixer, FFT, MQTT, WAV) verbatim. One language for the timing-critical audio path. Single process. |
| D2 | Front-end | **React + Vite + TypeScript** | Directed assumption confirmed. Vite for fast dev, TS for safety. No SSR needed — it's a LAN tool, so a plain SPA, not Next.js. |
| D3 | SPA hosting | Served as **static files by Kestrel** (single origin) | No second dev server, no CORS. `dotnet run` serves both API and UI. |
| D4 | Browser↔backend control transport | **REST** (commands) + **plain WebSocket** carrying JSON (state + events) | **(directed: plain WebSockets, no SignalR.)** REST for request/response actions; one JSON WS for server→browser push (device state, AEC status, spectrogram columns). |
| D5 | Realtime framework | **None (raw `System.Net.WebSockets`)** | **(directed: no SignalR.)** Avoids the SignalR dependency, protocol negotiation, and client lib. The message set is tiny and hand-rolled. |
| D6 | Browser↔backend audio transport | **Binary WebSocket**, frame format identical to the UDP audio frame (4-byte big-endian sequence index + PCM16) | Not WebRTC: it's a single hop and the payload is already simple PCM. Reusing the exact UDP frame layout lets `AudioMixer` and the existing framing run **unchanged** on both planes. |
| D7 | Simulated-device live audio | **Browser's** mic & speaker via `getUserMedia` + `AudioWorklet` | **(directed: full fidelity.)** Audio originates/plays wherever the browser runs, matching desktop behavior. |
| D8 | Sample-rate conversion | Run a dedicated browser **`AudioContext` at `sampleRate: 16000`** for both capture and playback; the browser resamples to/from hardware | Avoids hand-rolling 48k↔16k resamplers. Backend stays 16 kHz end-to-end as today. (Chrome honors this; see Risks for Safari.) |
| D9 | Uplink jitter buffer (browser→device) | **100 ms** buffer in the backend | **(directed.)** Implemented by instantiating the existing `AudioMixer` with `bufferInterval = 100 ms`; it already buffers and reorders by sequence index. Home network, so a modest buffer suffices to smooth WS jitter before relaying to UDP. |
| D10 | Downlink jitter buffer (device→browser) | Existing **100 ms** backend mixer + a small browser-side playback ring buffer (~150 ms) | Keeps current device→playback behavior; the browser buffer absorbs WS jitter on the way down. |
| D11 | Waveform / spectrogram | **Compute server-side** (reuse existing FFT/Hann/dB-normalize), push per-column intensity arrays over the JSON WS; **draw on `<canvas>`** | The DSP is portable and already separated from the draw step. Only the `WriteableBitmap` blit is replaced (~30 lines of canvas). No DSP rewrite, tiny bandwidth (~16 columns/sec). |
| D12 | Transport security | **HTTPS** via ASP.NET dev cert | **(directed: HTTPS is fine.)** Also required anyway — `getUserMedia` only works in a secure context on a non-localhost origin. |
| D13 | Persistence | **JSON files** beside the executable (or a configurable data dir); **WAV files** (`AEC Sample.wav` / `AEC Output.wav`, recorder dumps) in the working dir | Replaces `%AppData%\Intercom Test\Devices.json` and the `HKCU\Webathome\Intercom Test` registry prefs. Working-dir for WAVs matches the desktop app and is accepted. Cross-platform, trivially inspectable. |
| D14 | Configuration | **Environment variables** (`MQTT_HOST/PORT/USERNAME/PASSWORD`) + `appsettings.json` | Mirrors the existing env-var convention; no behavior change. |
| D15 | Dependency injection | **Built-in `Microsoft.Extensions.DependencyInjection`** (host builder) | The desktop app used manual `new`; the host gives clean lifetime management for the MQTT clients, UDP servers, and WS connection registry. |

---

## 5. Backend design

### 5.1 Reused from `IntercomServer.Utils` (unchanged)

- DTO / wire contract: `DeviceConfiguration`, `DeviceState`, `DeviceAction`,
  `DeviceLedAction`, `AudioConfiguration`, `Audio/AudioFormat`.
- `UdpAudioServer` — UDP send/receive + 4-byte sequence framing.
- `AudioMixer` + `Audio/AudioUtils.MixInBuffer` — jitter buffer + multi-source mixing
  (parameterized by buffer interval — used at 100 ms on both the downlink and uplink).
- `NetworkUtils` — local IP discovery (for the `add_endpoint` value).

### 5.2 Ported from IntercomTest (logic kept, UI dropped)

- **MQTT control client** (was `MainWindow`): subscribes to
  `intercom/client/+/configuration` and `.../state`, tracks the live device set,
  publishes `set/*` and `intercom/server/set/*`.
- **Simulated device** (was `IntercomClient` + `Device`): one MQTT identity per
  simulated device, advertises its UDP endpoint, responds to `set/*`, relays audio.
- **AEC service** (was `AECTestWindow`): UDP server on **5140**, record/play/clear of
  `AEC Sample.wav` / `AEC Output.wav`, drives the spectrogram producer.
- **Recorder service** (was `AudioRecorderServer`): UDP server on **5139**, dumps to
  timestamped WAVs.
- **Spectrogram/wave producer** (was `SoundRendering/*`): keep
  `BlockSoundRenderer`, the FFT + Hann window + magnitude→dB→normalize math; replace
  the `WriteableBitmap` blit with "emit one intensity column over the WS."

### 5.3 Dropped (Windows/WPF-only)

- All XAML and `BaseWindow` DWM dark-mode code.
- NAudio **WASAPI** capture/playback (replaced by browser audio). NAudio's **file**
  read/write (`WaveFileReader/Writer`) and resampling stay — they're cross-platform.
- Registry / `%AppData%` access (replaced by JSON files).

---

## 6. API surface

### 6.1 REST (commands; JSON request/response)

| Method & path | Purpose |
|---------------|---------|
| `GET /api/devices` | Snapshot of real + simulated devices (config + last state). |
| `POST /api/devices/{id}/volume` | Set playback volume. |
| `POST /api/devices/{id}/enabled` | Enable/disable. |
| `POST /api/devices/{id}/identify` | Identify. |
| `POST /api/devices/{id}/restart` | Restart. |
| `POST /api/devices/{id}/audio-config` | Push `AudioConfiguration`. |
| `DELETE /api/devices/{id}` | Remove. |
| `POST /api/sim-devices` / `DELETE /api/sim-devices/{id}` | Add/remove a simulated device. |
| `POST /api/sim-devices/{id}/action` | `click` / `long_click`. |
| `POST /api/server/doorbell` | Ring doorbell. |
| `POST /api/server/auto-accept` | Toggle auto-accept. |
| `POST /api/aec/{record\|play\|clear}` | AEC sample control. |
| `POST /api/aec/device` · `POST /api/aec/mode` | Select target device · waveform/spectrogram. |
| `POST /api/recorder` | Start/stop the audio dump server. |

Each handler is a thin pass-through to an MQTT publish or a service call — the MQTT
contract remains the single source of truth (no shadow state).

### 6.2 WebSocket: control/state (JSON, server→browser push) — `/ws/events`

One connection per browser tab. Server pushes:

```jsonc
{ "type": "device-state",  "id": "ab12…", "state": { /* DeviceState */ } }
{ "type": "device-config", "id": "ab12…", "config": { /* DeviceConfiguration */ } }
{ "type": "device-removed","id": "ab12…" }
{ "type": "aec-status",    "state": "idle|recording|playing", "hasSample": true }
{ "type": "spectrogram",   "column": [ /* 0..255 intensities, top→bottom */ ] }
{ "type": "waveform",      "amplitude": 0.0 }
```

### 6.3 WebSocket: audio (binary) — `/ws/audio/{simDeviceId}` and `/ws/audio/aec`

Frame = **4-byte big-endian sequence index + PCM16LE mono 16 kHz** (≈20 ms / frame),
identical to the UDP audio frame so backend code is shared.

- **Uplink** (browser→backend): mic frames → backend `AudioMixer(100 ms)` →
  `UdpAudioServer.Send` to the device endpoints.
- **Downlink** (backend→browser): device UDP → `AudioMixer(100 ms)` → WS frames →
  browser playback buffer.

---

## 7. Audio pipeline (detail)

### 7.1 Uplink — browser mic → device

1. `getUserMedia({ audio: { deviceId } })` into an `AudioContext({ sampleRate: 16000 })`.
2. An `AudioWorklet` accumulates 20 ms (320 samples) of Float32, converts to PCM16,
   prepends a 4-byte big-endian sequence index, sends as a binary WS frame.
3. Backend feeds frames into `AudioMixer(bufferInterval = 100 ms)` (D9), then a 20 ms
   timer `Take`s chunks and `UdpAudioServer.Send`s them (with MTU fragmentation,
   `1472 − 4` bytes) to every endpoint the device advertised.

### 7.2 Downlink — device → browser speaker

1. Device UDP → backend `UdpAudioServer` → `AudioMixer(100 ms)` (mixes multiple
   remote streams, as today).
2. A 20 ms timer `Take`s mixed audio and sends WS frames to the browser.
3. Browser buffers ~150 ms in a ring buffer, an `AudioWorklet` feeds the
   `AudioContext({ sampleRate: 16000 })` output; the browser resamples to hardware.

### 7.3 Why the frame format is shared

Keeping the WS audio frame byte-identical to the UDP audio frame means `AudioMixer`,
the sequence/reorder logic, and the framing in `UdpAudioServer` run unchanged on both
planes. The browser is just "another endpoint" that happens to be reached over a
WebSocket instead of UDP.

---

## 8. Waveform / spectrogram

The existing renderers already split **compute** from **draw**:

- **Spectrogram** (`BlockSize = 1024`): Hann window → `FastFourierTransform.FFT` →
  for the first 512 bins, magnitude → `20·log10` → normalize against `[-100, 0] dB`
  → 0..255 grayscale, high frequency at top.
- **Waveform** (`BlockSize = 200`): mean absolute amplitude per block.

Port: keep all of the above in C#. Replace `DrawSpectrogramColumn` /
`WaveRenderer.AddSamples` (the `WriteableBitmap` pointer writes + `MemoryCopy` scroll)
with "emit the computed column/amplitude over `/ws/events`." The browser keeps a
`<canvas>`, scrolls its `ImageData` left by the column width, and blits the new column
on the right — a direct, mechanical translation of the existing scroll-and-append.

Bandwidth: ~16 spectrogram columns/sec × 512 bytes ≈ 8 KB/s. Negligible.

---

## 9. Front-end (React) component map

| WPF window/control | React component | Notes |
|--------------------|-----------------|-------|
| `MainWindow` | `<Console>` | Toolbar (add device, doorbell, auto-accept, AEC) + two lists. |
| `IntercomClientControl` (simulated) | `<SimDeviceCard>` | Mic/speaker pickers via `navigator.mediaDevices.enumerateDevices()`; click/long-click; opens the audio WS. |
| `RealDeviceControl` | `<RealDeviceCard>` | Name, volume, LEDs, enabled/recording/playing, configure/identify/restart/remove. |
| `AECTestWindow` | `<AecView>` | Device picker, record/play/clear, spectrogram toggle, `<canvas>`, audio WS. |
| `AudioConfigurationWindow` | `<AudioConfigDialog>` | Numeric form + copy/paste JSON. |

State: device list + per-device state held in a small store (Zustand or plain
context/reducer — no heavy framework). Live updates arrive on `/ws/events`.

---

## 10. Reuse / rewrite / new

| Reused (C#, ~verbatim) | Rewritten (was WPF/Win) | New |
|------------------------|-------------------------|-----|
| DTO contract, MQTT topic logic | XAML UI → React SPA | REST command API |
| `UdpAudioServer`, framing | WASAPI mic/speaker → browser audio + WS bridge | `/ws/events` + `/ws/audio` |
| `AudioMixer`, `AudioUtils` | `WriteableBitmap` → `<canvas>` | Browser AudioWorklets + ring buffer |
| FFT / Hann / normalize math | registry + `%AppData%` → JSON files | DI host wiring |
| NAudio WAV I/O + resampling | `BaseWindow` DWM dark mode → dropped | |

---

## 11. Proposed solution layout

```
IntercomServer.sln
├── IntercomServer/            (unchanged backend service + ChatGPT/MCP)
├── IntercomServer.Utils/      (unchanged shared library — referenced by the new project)
├── IntercomTest/              (existing WPF app — kept until the web app reaches parity)
└── IntercomTestWeb/           (NEW: ASP.NET Core host + audio bridge)
    ├── IntercomTestWeb.csproj (refs IntercomServer.Utils)
    ├── Services/              (ported MQTT/AEC/recorder/sim-device/spectrogram services)
    ├── Endpoints/             (REST + WebSocket handlers)
    ├── SoundRendering/        (ported compute-only renderers)
    └── ClientApp/             (Vite + React + TS; built into wwwroot)
```

Suggested project name: **`IntercomTestWeb`** (rename freely). Keep the WPF project
in the solution until the web app is verified at parity, then retire it.

---

## 12. Security & runtime

- **HTTPS** via `dotnet dev-certs https --trust`; Kestrel binds `0.0.0.0` so the LAN
  hostname works. Required regardless for `getUserMedia`.
- Dev-only: no auth, no rate limiting, no hardening. Single trusted operator.
- Config via env vars (`MQTT_HOST/PORT/USERNAME/PASSWORD`) + `appsettings.json`.
- Fixed UDP ports unchanged: **5139** (recorder), **5140** (AEC); simulated devices
  use ephemeral ports.

---

## 13. Risks & open questions

- **AudioWorklet capture/playback + drift** — the one piece of genuinely new
  engineering. The 100 ms uplink and ~150 ms downlink buffers absorb most of it; for a
  LAN test tool a simple ring buffer is sufficient.
- **`AudioContext` sampleRate: 16000** — honored by Chrome; Safari historically
  ignores arbitrary rates. Mitigation: target Chrome (dev tool), or add a JS resampler
  fallback. (`IntercomServer/ChatGpt/Audio/StreamingResampler.cs` is a portable
  reference if parity is ever wanted.)
- **Browser tab backgrounding** can throttle timers/audio — a known browser quirk,
  acceptable for a foreground test tool.

---

## 14. Out of scope

The ChatGPT / OpenAI Realtime / MCP conversation stack lives entirely in
`IntercomServer` and is unaffected. IntercomTest only ever *triggered* it via a device
`long_click`; the web app does the same. No conversation logic moves into the web app.

---

## Appendix A — MQTT topic contract

```
intercom/client/<deviceId>/configuration   (retained, device→) DeviceConfiguration JSON
intercom/client/<deviceId>/state           (retained, device→) DeviceState JSON, LWT {online:false}
intercom/client/<deviceId>/set/<x>          (→device) recording, red_led, green_led,
                                              add_endpoint, remove_endpoint, volume,
                                              identify, restart, enabled, audio_config, action
intercom/server/set/<x>                     (→server) ring_doorbell, auto_accept
```

JSON convention: `snake_case`, ignore-null. (Today duplicated in `IntercomClient` and
`Device`; the web app should centralize one `JsonSerializerOptions`.)

## Appendix B — Audio constants

```
Format       : 16 kHz, 16-bit, mono PCM
Chunk        : 20 ms
UDP frame    : 4-byte big-endian sequence index + PCM payload, ≤ 1472−4 bytes
Downlink buf : 100 ms (existing AudioMixer)
Uplink buf   : 100 ms (AudioMixer, this design)
AEC port     : 5140   Recorder port : 5139
```
