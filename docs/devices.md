# Devices, sounds and the doorbell

How the server finds its devices, what it reads from them and sends them, and which devices the
doorbell and a call reach. The ChatGPT conversation a device can hold is in
[`CHATGPT_MCP.md`](CHATGPT_MCP.md).

## Finding devices

Devices publish under an MQTT topic prefix, as `<prefix>/<device_id>/…`. The server reads devices
under every prefix in `DEVICES_TOPIC_PREFIXES`: the intercoms' `intercom/client` and the
ToiletAutomation devices' `toilet_automation` by default. Under each prefix it subscribes to
`<prefix>/+/state`, `ready`, `configuration` and `set/action`, and it sends a device its commands
(`set/red_led`, `set/green_led`, `set/recording`, `set/add_endpoint`, `set/remove_endpoint`) under
the prefix it found the device under. A device is known by its id alone, so an id names one device
across every prefix.

- **`configuration`**, retained, holds the device's UDP `endpoint` (`ip:port`) and its
  `audio_formats`: `in` for its microphone and `out` for what it plays, each a channel layout,
  sample rate and bit rate.
- **`state`** holds `online`, and `enabled` for an intercom. A device whose state has no `enabled`
  is not enabled.
- An empty `configuration` or `state` removes the device.

Home Assistant publishes to the server itself: `intercom/server/set/ring_doorbell` and
`intercom/server/set/auto_accept`.

## The microphone

A device that advertises an `audio_formats.in`, in any format, has a microphone; one that
advertises none, or has no configuration, has none. A device without a microphone takes no part
in a call or a ChatGPT conversation: a call request does not ring it, and its click and long click
do nothing but log `Device <id> has no microphone, so its <action> does nothing`.

A device **can take a call** when it is online, enabled and has a microphone. A call request
rings the other devices that can take a call and are not in a ChatGPT conversation.

## The doorbell

`intercom/server/set/ring_doorbell` with the payload `true` plays the doorbell on every online
device that can take a call, one in a ChatGPT conversation included, and on every online device
that `DOORBELL_EXTRA_DEVICES` names, enabled or not, with a microphone or without. That is how a
ToiletAutomation device, which has no microphone and no `enabled`, gets the doorbell.

## What a device is sent

Everything the server sends a device, the doorbell, the ring, the rejected tone and a ChatGPT
reply, is PCM in the format the device advertises as `audio_formats.out`, when that is mono
16-bit at 8 to 192 kHz. A device that advertises no `out`, or one outside that, or no endpoint the
server can parse, is sent no sound, and the server logs a warning naming it.

- **Packets.** The server sends 20 ms of the device's format at a time (640 bytes at mono 16 kHz),
  paced in real time, and splits a chunk past 1468 bytes into datagrams of at most 1468 (at
  48 kHz, 1468 and 452 bytes). Each datagram is a 4-byte big-endian packet index, then the PCM.
- **The counter.** Each device's datagrams are numbered by a counter of its own. It starts at
  zero the first time the server sends to the device, and runs on across everything sent to it,
  from any stream, while the server runs.
- **A playback per device.** A sound started on some devices replaces what those devices were
  playing, and nothing else. A ChatGPT reply is a stream of its own, so a doorbell plays beside
  it, both on the device's counter.

## The sounds

The doorbell, the ring and the rejected tone are MP3s built into the server, and FFmpeg decodes
them: the `ffmpeg` on the server's `PATH`, which the image installs. At its start the server
decodes all three into mono 16 kHz 16-bit and logs `Decoded the built-in sounds with FFmpeg: …`
with their lengths. A server whose FFmpeg is missing or cannot decode them stops there, naming the
cause. A sound for a device in another format is decoded the first time it is played in that
format, and kept.

## Settings

Environment variables, read at the start; unset or empty, a setting takes its default. A value the
table does not allow stops the server at its start, naming the setting.

| Variable | Default | Meaning |
| --- | --- | --- |
| `DEVICES_TOPIC_PREFIXES` | `intercom/client,toilet_automation` | The topic prefixes devices publish under, comma-separated, each entry trimmed. Each is one or more topic levels separated by `/`, none of them empty or holding `+` or `#`. None may lie under another, and none may be, lie under or hold `intercom/server`, the server's own topics. |
| `DOORBELL_EXTRA_DEVICES` | none | Device ids the doorbell also rings while they are online, comma-separated, each entry trimmed. Each is one topic level: not empty, and holding no `/`, `+` or `#`. |
