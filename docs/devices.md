# Devices, sounds and the doorbell

How the server finds its devices, what it reads from them and sends them, and which devices the
doorbell and a call reach. The ChatGPT conversation a device can hold is in
[`CHATGPT_MCP.md`](CHATGPT_MCP.md), and the ambient sound and its Home Assistant switch in
[`ambient-sound.md`](ambient-sound.md).

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

Home Assistant publishes to the server itself: `intercom/server/set/ring_doorbell`,
`intercom/server/set/auto_accept`, and each ambient switch's
`intercom/server/devices/<device_id>/set/ambient`. The server says, retained, on
`intercom/server/status` whether it is connected: `online` once it has subscribed after each
connect, and `offline` at a clean stop or, by its MQTT last will, once the broker has lost the
connection.

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

Everything the server sends a device, the doorbell, the ring, the rejected tone, a ChatGPT
reply and the ambient sound, is PCM in the format the device advertises as `audio_formats.out`,
when that is mono 16-bit at 8 to 192 kHz. A device that advertises no `out`, or one outside that,
or no endpoint the server can parse, is sent no sound, and the server logs a warning naming it.

- **Packets.** A stream writes 20 ms of the device's format at a time (640 bytes at mono
  16 kHz), less only at a sound's end, a loop's wrap and, in a ChatGPT reply, the end of each
  piece of audio OpenAI sends. It is paced in real time, the ambient sound a prebuffer ahead
  ([`ambient-sound.md`](ambient-sound.md)). Each write goes as the fewest datagrams of at most
  1468 bytes of PCM that carry whole frames, their sizes differing by one frame at most: at
  48 kHz, two of 960 bytes. The ToiletAutomation firmware fills a lost or late packet with the
  stream's mean packet size, which equal packets keep exact. Each datagram is a 4-byte
  big-endian packet index, then the PCM.
- **The counter.** Each device's datagrams are numbered by a counter of its own. It starts at
  zero the first time the server sends to the device, and runs on across everything sent to it,
  from any stream, while the server runs.
- **A playback per device.** A sound started on some devices replaces what those devices were
  playing, and nothing else. A ChatGPT reply is a stream of its own, so a doorbell plays beside
  it, both on the device's counter. The ambient sound is a stream of its own too, and pauses
  while any of the others plays to the device.

## The sounds

The doorbell, the ring and the rejected tone are MP3s built into the server, and FFmpeg decodes
them: the `ffmpeg` on the server's `PATH`, which the image installs. At its start the server
decodes all three into mono 16 kHz 16-bit and logs `Decoded the built-in sounds with FFmpeg: …`
with their lengths. A server whose FFmpeg is missing or cannot decode them stops there, naming the
cause. A sound for a device in another format is decoded the first time it is played in that
format, and kept. FFmpeg reads every input through its cache protocol, which keeps what it has
read in a temporary file and seeks in that, so an M4A whose index follows its audio, as phones
write them, decodes from a stream: the server needs a writable `/tmp`.

## Settings

Environment variables, read at the start; unset or empty, a setting takes its default. A value the
table does not allow stops the server at its start, naming the setting.

| Variable | Default | Meaning |
| --- | --- | --- |
| `DEVICES_TOPIC_PREFIXES` | `intercom/client,toilet_automation` | The topic prefixes devices publish under, comma-separated, each entry trimmed. Each is one or more topic levels separated by `/`, none of them empty or holding `+` or `#`. None may lie under another, and none may be, lie under or hold `intercom/server`, the server's own topics. One without `toilet_automation` needs an `AMBIENT_TOPIC_PREFIXES` of its own ([`ambient-sound.md`](ambient-sound.md)). |
| `DOORBELL_EXTRA_DEVICES` | none | Device ids the doorbell also rings while they are online, comma-separated, each entry trimmed. Each is one topic level: not empty, and holding no `/`, `+` or `#`. |
