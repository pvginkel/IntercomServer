# IntercomTestWeb

A web-based replacement for the WPF **IntercomTest** desktop tool. ASP.NET Core (net9.0) backend +
React/Vite SPA, single process, single origin. See `docs/INTERCOM_TEST_WEB.md` for the full
architecture and the phase plan.

**This is Phase A — control plane + UI, no audio.** Implemented:

- MQTT control client: discovers real devices on the bus, tracks their configuration/state.
- REST command API (§6.1) + the `/ws/events` JSON push channel (§6.2).
- Simulated-device MQTT presence: each sim device has its own MQTT identity, advertises a UDP
  endpoint, and responds to `set/*` (LED / recording / endpoints) — but does **not** relay audio yet.
- JSON-file persistence: `data/Devices.json` (sim devices) and `data/settings.json` (auto-accept).
- React UI: console with the toolbar, real-device cards, sim-device cards, and the audio-config dialog.

Deferred: the live audio bridge (Phase B) and the AEC tool + spectrogram/recorder (Phase C).

## Running

Configuration is via environment variables (D14):

| Variable | Default | Notes |
|----------|---------|-------|
| `MQTT_HOST` | — | **Required** (broker hostname/IP). |
| `MQTT_PORT` | MQTT default | |
| `MQTT_USERNAME` / `MQTT_PASSWORD` | — | Optional broker credentials. |
| `HTTP_PORT` | `8080` | Kestrel binds `0.0.0.0:$HTTP_PORT` so the LAN hostname works. |
| `DATA_DIR` | `data` | Where `Devices.json` / `settings.json` are written. |

```sh
MQTT_HOST=192.168.1.10 dotnet run --project IntercomTestWeb
# then browse http://<this-host>:8080/
```

A `dotnet build`/`run` builds the SPA into `wwwroot` automatically (see the `BuildSpa` MSBuild
target). TLS is out of scope and owned by the operator (D12); this is a plain-HTTP service.

## Front-end development

For HMR while iterating on the React app, run the Vite dev server (it proxies `/api` and `/ws` to
the backend on `:8080`) and start the backend separately with the SPA build skipped:

```sh
cd IntercomTestWeb/ClientApp && npm install && npm run dev     # Vite on :5173
MQTT_HOST=192.168.1.10 dotnet run --project IntercomTestWeb -p:SkipSpaBuild=true
```

`npm run typecheck` runs `tsc` (the production `npm run build` uses esbuild and does not type-check).
