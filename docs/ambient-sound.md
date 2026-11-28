# Ambient sound

The server plays ambient sound on the devices under `AMBIENT_TOPIC_PREFIXES`, the
ToiletAutomation devices by default: one track at a time on each, levelled, faded in, looped and
faded out. Home Assistant starts and stops it through a switch the server publishes on each
device's own page, and the device routes of the API do the same. The tracks are the ones the
server keeps under `<DATA_DIR>/ambient`, in [`web-host.md`](web-host.md). How the server finds a
device, and the packets and counter every sound reaches it in, are in [`devices.md`](devices.md).

## A playback

A device has one ambient playback at most, kept by its device id.

- **The start** needs a device under a prefix of `AMBIENT_TOPIC_PREFIXES` that is online and
  advertises an endpoint the server can parse and an `audio_formats.out` of mono 16-bit PCM at 8
  to 192 kHz. A start that names a track plays it, enabled or disabled. One that names none
  plays an enabled track picked at random, skipping the track the device's latest playback
  played unless it is the only enabled one. The server remembers that track in memory alone, so
  a restart forgets it. The pick and the open run under the track store's lock, so the track
  picked is the one played.
- **The decode.** One FFmpeg run decodes the whole track into memory, mixed down to mono and
  resampled to the device's rate at 16-bit, and measures it with FFmpeg's EBU R128 meter. The
  playback plays that copy, so a track renamed, disabled or deleted meanwhile plays on as it
  was, under the name it started with.
- **The level.** The gain brings the track's integrated loudness to `AMBIENT_LOUDNESS_LUFS`,
  held down so that its sample peak stays at −1 dBFS or below. The start logs `Playing ambient
  track <track> on device <device_id> at a gain of <gain> dB`.
- **The sound** fades in from silence over `AMBIENT_FADE_MS` and loops without a gap: a chunk
  that reaches the track's end goes on from its start, so the packets keep their size across
  the seam.
- **The lead.** The stream runs `AMBIENT_PREBUFFER_MS` ahead of real time. Its first chunks go
  out faster than real time, until the device holds the prebuffer 100 ms into the stream, and it
  then stays that far ahead. What the device still holds when the stream starts, a previous
  playback's lead among it, counts toward the prebuffer. The server's other sounds are paced in
  real time.
- **The endpoint.** Each chunk goes to the endpoint the device advertises at that moment, so a
  device that re-advertises a new address gets the next chunk there. A device that no longer
  advertises an endpoint, or advertises another format, ends the playback at once, with a
  warning.

## Stops

A stop fades the sound out over `AMBIENT_FADE_MS`, from the level it had reached, and the
playback ends; the device plays out what it holds. A stop comes from:

- the switch's `OFF`, or `DELETE /api/devices/{device_id}/ambient`;
- the cap, `AMBIENT_CAP_SECONDS` after the sound began, counted in real time, a pause included;
- the device's state, once its `online` is not true;
- the device's removal, by an empty `configuration` or `state`;
- the server's shutdown, which waits for every playback to end before the server disconnects.

A stop that comes while a start is still decoding abandons the start, FFmpeg with it. A start on
a device that is playing, or starting to, changes nothing: it answers as that playback's start
does, whatever track it names. A start on a device whose playback is fading out makes a new
playback, whose sound begins once the old one has ended.

## The pause

The server's other sounds pause the ambient sound: the doorbell, the ring, the rejected tone,
and a ChatGPT conversation, from its start to its end, between the replies too. While any of
them plays to the device, the ambient stream sends nothing. It pauses where it is, with no
fade-out, and what it had already sent ahead plays before that sound. Once the last of them has
ended, it resumes from the same point of the track as it starts: with the fade-in, and a lead
topped up to the prebuffer, counting what the device still holds of the other sound.

A stop during the pause ends the playback with nothing more sent, and a pause during the
fade-out ends it at once. The switch stays on through a pause.

## The Home Assistant switch

For each device under a prefix of `AMBIENT_TOPIC_PREFIXES` whose configuration carries a
`unique_id`, the server publishes, through Home Assistant's MQTT discovery, a switch "Ambient
sound" and a sensor "Ambient track", which holds the name of the track playing and is empty when
none is. Their `device.identifiers` is that `unique_id`, the identifier the firmware gives its
own device, so both show on the device's own page, and Home Assistant names each from the
device's name and its own. A device whose configuration has no `unique_id` gets no switch, and
the server logs a warning.

| Topic | Retained | Published by | Payload |
| --- | --- | --- | --- |
| `homeassistant/switch/intercom_server/<device_id>_ambient_sound/config` | yes | the server | the switch's discovery, its `unique_id` `intercom_server_<device_id>_ambient_sound` |
| `homeassistant/sensor/intercom_server/<device_id>_ambient_track/config` | yes | the server | the sensor's discovery, its `unique_id` `intercom_server_<device_id>_ambient_track` |
| `intercom/server/devices/<device_id>/state` | yes | the server | `{"ambient":"ON","track":"<name>"}` or `{"ambient":"OFF"}` |
| `intercom/server/devices/<device_id>/set/ambient` | no | Home Assistant | `ON` or `OFF` |

- **The node id** is `intercom_server`, never the device id: the firmware empties, on every
  connect, each retained discovery config under its own node id that it did not publish itself.
- **Discovery and state** go out each time the server reads the device's configuration, so at
  its start, as each device's retained configuration arrives, every switch is published off: a
  restart resumes no playback. The state goes out again as a playback's sound begins and as the
  playback ends, whatever started or stopped it, and as a start from the switch is refused, so a
  refused `ON` reads off. The publications go out one at a time, each with what holds when it
  goes, so the last one out holds what holds now.
- **The commands.** `ON` starts a playback of a track picked at random, and `OFF` stops it,
  neither holding up the server's other messages. A retained command, one the broker kept from
  before the server subscribed, is ignored, so a stale `ON` cannot start sound after a restart.
  A command for a device the server does not know is ignored too.
- **Availability.** Both entities are available while the server and the device are online:
  `intercom/server/status` reads `online` ([`devices.md`](devices.md)), and the device's own
  `<prefix>/<device_id>/state` has `online` true. A device that goes offline keeps its switch,
  shown unavailable.
- **Removal.** A device removed by an empty `configuration` or `state` has its two discovery
  configs and its state emptied, retained, so Home Assistant drops both entities.

## The device routes

Under `/api/devices`, for the role `user` ([`web-host.md`](web-host.md) has the sign-in). A
device is named in the path by its device id, and answered as JSON:

```json
{"id":"0x5f1c","prefix":"toilet_automation","microphone":false,"online":true,"format":{"channel_layout":"mono","sample_rate":48000,"bit_rate":16},"track":"Rain on the skylight"}
```

`format` is the format the server sends the device, null when it advertises none the server can
produce, and `track` the ambient track it plays, null when it plays none.

| Method and path | Request | Answer |
| --- | --- | --- |
| `GET /api/devices` | | 200, a JSON array of every device the server knows, online or not, sorted by id |
| `PUT /api/devices/{device_id}/ambient` | no body, or JSON `{"track":"<name>"}` | 200, the device, once its sound has begun |
| `DELETE /api/devices/{device_id}/ambient` | | 204, an empty body, once the playback has faded out and ended, or at once when there is none |

The errors, each a problem whose `detail` says which:

- **400**: a start whose body is not a JSON object with an optional `track`.
- **404**: no device has the id; no track has the name a start gives.
- **409**: a start the player refuses: a device that is not under `AMBIENT_TOPIC_PREFIXES`, is
  offline, or advertises no endpoint or format the server can send to; no enabled track; a
  track FFmpeg cannot decode, the `detail` naming FFmpeg's error, or one that decodes to no
  audio; a start a stop abandoned.

Any other failure of a start, such as a read of the track's file that fails, is logged as an
error and answers 500.

## Settings

Environment variables, read at the start; unset or empty, a setting takes its default. A value
that is not a number where the table asks for one stops the server at its start, and so does a
value the table does not allow, naming the setting.

| Variable | Default | Meaning |
| --- | --- | --- |
| `AMBIENT_TOPIC_PREFIXES` | `toilet_automation` | The topic prefixes whose devices get the ambient sound and the switch, comma-separated, each entry trimmed. Each must be one of `DEVICES_TOPIC_PREFIXES`, so a `DEVICES_TOPIC_PREFIXES` without `toilet_automation` needs this set too. |
| `AMBIENT_LOUDNESS_LUFS` | `-20` | The integrated loudness, in LUFS, every track is levelled to. |
| `AMBIENT_FADE_MS` | `500` | How long, in whole milliseconds, a playback fades in at its start and at each resume, and out at its stop: 0 or more, 0 for no fade. |
| `AMBIENT_PREBUFFER_MS` | `500` | How far, in whole milliseconds, the stream runs ahead of real time: 0 or more, 0 for none. |
| `AMBIENT_CAP_SECONDS` | `1800` | The longest a playback plays, in whole seconds from its sound's start, before it fades out: 1 to 4294967. |
