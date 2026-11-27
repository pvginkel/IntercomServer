# The web host: health, sign-in, the ambient tracks and app updates

The server's HTTP side: its port and probes, the Keycloak sign-in every `/api` route takes, the
ambient tracks it keeps and their API, and the update channel that publishes and serves the app's
releases. How the deploy publishes the port is pvginkel/IntercomDeploy's. The devices' MQTT and
UDP are in [`devices.md`](devices.md).

## The web host

ASP.NET Core's Kestrel, inside the server's host, listens on `HTTP_PORT` on every address. It
starts first, before the sounds are decoded and the MQTT connection is made, so it answers while
the server is still connecting to the broker. Its start logs `Now listening on: http://[::]:8080`.
The web host reads no `ASPNETCORE_` variable: `HTTP_PORT` alone sets its port.

The framework's own log reaches the server's at warnings and errors, besides that line, so a
request that goes well is not logged. The track store and the update channel log what they
change.

- **`GET /healthz`**, liveness: `ok`, 200, whenever the web host answers.
- **`GET /readyz`**, readiness: `mqtt: connected`, 200, once the server is connected to the
  broker and subscribed; `mqtt: disconnected`, 503, otherwise. Liveness does not depend on MQTT,
  so a broker outage, which the server recovers from on its own, never reads as a dead process.

## Sign-in

Every route under `/api` takes a Keycloak bearer token, `Authorization: Bearer <token>`, from the
realm `KEYCLOAK_ISSUER` names. A token is accepted when it is signed by one of the realm's keys,
which the server reads from the realm's discovery document at the first token, names that issuer
as its `iss` and `KEYCLOAK_AUDIENCE` among its `aud`, and has not expired.

A token's roles are its client roles on the audience's client alone,
`resource_access["<KEYCLOAK_AUDIENCE>"].roles`: realm roles and the roles of other clients count
for nothing. Each route asks for one role:

| Role | Who holds it | Routes |
| --- | --- | --- |
| `user` | the app's user | the ambient tracks, `/api/ambient-tracks` |
| `pipeline` | Jenkins' service account | the publish, `POST /api/app-releases` |

A request without a valid token answers 401, with an empty body and `WWW-Authenticate: Bearer`;
one whose token lacks the route's role answers 403, with an empty body. `/updates` asks for no
token, and one it is sent does not matter.

Under `/api`, a route's own refusal, any but 401 and 403, is `application/problem+json`, its
`detail` a sentence saying what is wrong, such as `The name 'RAIN' is taken, by enabled/Rain.m4a.`

## The ambient tracks

### On disk

The tracks are files under `<DATA_DIR>/ambient`, which the server creates at its start:

```
<DATA_DIR>/ambient/enabled/Rain on the skylight.m4a
<DATA_DIR>/ambient/disabled/Kids singing.m4a
<DATA_DIR>/ambient/.incoming/      uploads in progress
```

- **A track's name is its file name without its extension**, and its extension is its format's,
  as ffprobe names it: `m4a` for the MP4 family, `webm` for Matroska and WebM, and for any other
  the first name ffprobe gives the format (`mp3`, `wav`, `ogg`…).
- **A name** is 1 to 80 characters, with none of `/ \ : * ? " < > |`, no control characters and
  no space or dot at either end. It is unique across both folders, ignoring case and extension,
  and every lookup by name ignores case.
- **An upload** is written under `.incoming/`, probed with ffprobe, and only then renamed into
  `enabled/`, so a half-written upload is never a track. A new track is enabled. The start deletes
  what an earlier process left in `.incoming/`.
- **Rename** is a rename in the track's folder, **enable and disable** move it between the
  folders, and **delete** removes it. They run under one lock, with the list, a lookup and an
  upload's last step, the name check and the rename.
- **Duration** is ffprobe's, kept in memory by path until the file's modification time changes.
  So the first list or lookup after a start probes every file once.

The folders are the data volume's, and can be written by hand. A file in them is a track only when
its name keeps the rules, no other file in either folder has its name, and ffprobe reads an audio
stream and a length in it. Any other file is left out of every list and lookup, and logged as a
warning when it is first found, `<path> is no ambient track. <why>`; its name is still taken for
an upload or a rename.

The server probes with the `ffprobe` on its `PATH`, which the image installs with FFmpeg. At its
start it logs `Found ffprobe <version>; the ambient tracks are in <folder>`; a server whose ffprobe
is missing stops there, with `ffprobe is missing: …`.

### The API

Under `/api/ambient-tracks`, for the role `user`. A track is named in the path by its name,
URL-encoded, and answered as JSON, its `duration` its length in seconds:

```json
{"name":"Rain on the skylight","enabled":true,"duration":134.2}
```

| Method and path | Request | Answer |
| --- | --- | --- |
| `GET /api/ambient-tracks` | | 200, a JSON array of every track, sorted by name, ignoring case |
| `POST /api/ambient-tracks` | `multipart/form-data`: first the part `name`, then the part `file` | 201, the track, with `Location: /api/ambient-tracks/<name, URL-encoded>` |
| `PATCH /api/ambient-tracks/{name}` | JSON: `name`, `enabled`, or both | 200, the track as it now is |
| `DELETE /api/ambient-tracks/{name}` | | 204, an empty body |
| `GET /api/ambient-tracks/{name}/audio` | `Range` optional | 200, or 206 for a range, the file, with `Accept-Ranges: bytes` |

A `PATCH` renames and enables or disables in one move; a field it leaves out stays as it is. The
audio of a disabled track is served too, and its type comes from the file's extension:
`audio/mp4` for `m4a`, `audio/mpeg` for `mp3`, `audio/wav` for `wav`, `video/webm` for `webm`,
`application/octet-stream` for one with no known type. A download reads on to its end when the
track is renamed, moved or deleted meanwhile.

The errors, each a problem whose `detail` says which:

- **400**: an upload that is not a form of `name`, then `file`; a change that is not a JSON object
  of `name`, `enabled` or both; a name that breaks the rules; a file ffprobe reads no audio, or no
  length, in.
- **404**: no track has the name.
- **409**: another file in the folders has the name an upload or a rename asks for.
- **413**: a file that holds more than `TRACK_MAX_BYTES`, or lasts longer than
  `TRACK_MAX_SECONDS`. Both limits are inclusive.

The upload route sets no request size limit of its own: the file streams into the store, which
stops reading it once it holds more than `TRACK_MAX_BYTES`, and the route answers 413. Past a
request size limit, Kestrel would close the connection instead.

## App updates

The app's releases are `<DATA_DIR>/updates/<version_code>.apk`, and the server keeps the newest
three.

- **`POST /api/app-releases`**, for the role `pipeline`, publishes one: a form of `version_code`,
  a positive 32-bit integer in decimal digits, and `apk`, a non-empty file. It answers 201 with an
  empty body; 400 for a body that is no such form, or a field missing or malformed; 409 for a
  version code published already, writing nothing. The APK is written to a temporary file in the
  folder and renamed into place, so a half-written one is never offered, and the releases past
  the newest three are deleted. The route's request size limit is 128 MiB.
- **`GET /updates?action=version`**, with no token, answers the highest version code as
  `text/plain`, or an empty body with no release.
- **`GET /updates?action=download`**, with no token, answers that release's APK as
  `application/vnd.android.package-archive`, with
  `Content-Disposition: attachment; filename=<version_code>.apk`; 404 with no release.

Any other `action`, matched case-sensitively, or none, answers an empty 404. `/updates` takes no
token so that a build whose sign-in is broken can still update itself. A file in the folder not
named as a publish writes it (`012.apk`, `0.apk`, `beta.apk`) is not offered.

## Settings

Environment variables, read at the start; unset or empty, a setting takes its default. A value that
is not a number where the table asks for one, a limit that is not positive, or an issuer that is
not an HTTPS URL stops the server at its start.

| Variable | Default | Meaning |
| --- | --- | --- |
| `HTTP_PORT` | `8080` | The TCP port the web host listens on, on every address. |
| `KEYCLOAK_ISSUER` | none: required | The Keycloak realm's issuer URL, over HTTPS, exactly as its tokens carry it in `iss` (Keycloak's has no trailing slash). The server reads the realm's signing keys from its discovery document. |
| `KEYCLOAK_AUDIENCE` | `intercom-server` | The client the tokens are for: a token must name it in its `aud`, and its client roles on it are the server's roles. |
| `DATA_DIR` | `data` | The folder the server keeps its data in: the ambient tracks under `ambient/`, the app releases under `updates/`, and the ChatGPT bridge's memories under `memories/` ([`CHATGPT_MCP.md`](CHATGPT_MCP.md)). |
| `TRACK_MAX_SECONDS` | `900` | The longest a track may last, in whole seconds, as ffprobe reads it. |
| `TRACK_MAX_BYTES` | `52428800` | The most bytes a track's file may hold: 50 MiB. |
