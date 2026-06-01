# SEED-NOTES — IntercomServer

First architecture artifact (batch 2 of the producer backfill). See
`../../BATCH-2-FINDINGS.md` and `../../REVIEW-2.md` for the shared rationale; this
file records what is specific to this repo.

- **Producer id:** `intercom-server`  ·  **introduced:** `2025-02-17` (repo's first commit, full clone).
- **What this repo owns:** the Intercom Server product («SoftwareProduct» ApplicationComponent, `registry:5000/intercom-server`).
- **Realizes the intercom relay service.** The intercom protocol IS a fixed MQTT topic contract (`intercom/client/<id>/...`, `intercom/server/...`) hardcoded in both server and device. Modeled as `svc:intercom` (ApplicationService) realized by the server, exposed via `if:intercom-mqtt` (ApplicationInterface = the topic namespace) that the Intercom device consumes by UUID. (D4: operator wanted the topic contract modeled as an interface rather than left implicit.)
- **Transport edge:** → `cap:pub-sub-broker` `boundBy: env:MQTT_HOST` (helm-deployed backend, substitutable infra — batch-1 convention). The relay service rides this broker. No HTTP API; no OIDC.
- **Note:** not currently in HelmCharts; the `boundBy` recipe dangles (reported, not fatal) until a deployer renders it.
- **Validation:** `./scripts/arch-validate.py docs/architecture/architecture.yaml` → OK.
