// Browser side of the Phase B audio bridge for one simulated device. Mirrors the WPF
// IntercomClientControl audio path, but the mic/speaker live in the browser:
//
//   Uplink   : getUserMedia -> AudioContext(16k) -> capture worklet -> 4-byte BE seq + PCM16 -> WS.
//              Only sent while the device is "recording" (server-gated, exactly like the WPF app).
//   Downlink : WS frame (4-byte seq + PCM16) -> Float32 -> playback worklet (~150 ms ring) -> speaker.
//
// One AudioContext is run at sampleRate 16000 so capture and playback are 16 kHz end-to-end and the
// browser handles hardware resampling (D8). Chrome honors the requested rate; see the spec's Risks.

// AudioContext.setSinkId is not yet in the TS DOM lib; describe just what we use.
type SinkContext = AudioContext & { setSinkId?: (id: string) => Promise<void> };

// Picker value that disables a channel (no mic capture / no playback), mirroring the WPF app's blank
// device entry. Channels default to off; selecting a specific device id enables that channel.
export const AUDIO_OFF = 'off';

export interface AudioDevices {
  mics: MediaDeviceInfo[];
  speakers: MediaDeviceInfo[];
}

// The mic is requested automatically on load (AudioGate), not from a click, so a context created
// afterwards can start suspended under the browser autoplay policy. Resume any such context on the
// first page interaction — a single shared listener covers every device's context.
const pendingResume = new Set<AudioContext>();
let resumeListenerInstalled = false;

function resumeOnFirstGesture(ctx: AudioContext): void {
  pendingResume.add(ctx);
  if (resumeListenerInstalled) return;
  resumeListenerInstalled = true;

  const handler = () => {
    for (const c of pendingResume) void c.resume().catch(() => {});
    pendingResume.clear();
    resumeListenerInstalled = false;
    document.removeEventListener('pointerdown', handler);
    document.removeEventListener('keydown', handler);
  };

  document.addEventListener('pointerdown', handler);
  document.addEventListener('keydown', handler);
}

export async function listAudioDevices(): Promise<AudioDevices> {
  const all = await navigator.mediaDevices.enumerateDevices();
  return {
    mics: all.filter((d) => d.kind === 'audioinput'),
    speakers: all.filter((d) => d.kind === 'audiooutput'),
  };
}

export class SimAudioSession {
  private ctx?: SinkContext;
  private ws?: WebSocket;
  private stream?: MediaStream;
  private capture?: AudioWorkletNode;
  private playback?: AudioWorkletNode;
  private seq = 0;
  private recording = false;
  private closed = false;

  constructor(
    private readonly deviceId: string,
    private readonly micId: string,
    private readonly speakerId: string
  ) {}

  async start(): Promise<void> {
    const micEnabled = this.micId !== AUDIO_OFF;
    const speakerEnabled = this.speakerId !== AUDIO_OFF;

    // Both channels off: nothing to bridge, so skip the socket and AudioContext entirely.
    if (!micEnabled && !speakerEnabled) return;

    if (micEnabled) {
      if (!navigator.mediaDevices?.getUserMedia) {
        throw new Error('Microphone access requires HTTPS or localhost.');
      }
      // The empty string selects the default mic; a specific id selects that device.
      this.stream = await navigator.mediaDevices.getUserMedia({
        audio: this.micId ? { deviceId: { exact: this.micId } } : true,
      });
      if (this.closed) return this.stop();
    }

    const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(
      `${protocol}://${location.host}/ws/audio/${encodeURIComponent(this.deviceId)}`
    );
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    ws.onmessage = (event) => this.onDownlink(event.data as ArrayBuffer);
    await waitForOpen(ws);
    if (this.closed) return this.stop();

    const ctx: SinkContext = new AudioContext({ sampleRate: 16000 });
    this.ctx = ctx;
    if (speakerEnabled) await ctx.audioWorklet.addModule('/worklets/playback-processor.js');
    if (micEnabled) await ctx.audioWorklet.addModule('/worklets/capture-processor.js');
    if (this.closed) return this.stop();

    // Downlink: playback worklet -> speaker. Skipped when the speaker is off; downlink frames are
    // then dropped in onDownlink (no playback node).
    if (speakerEnabled) {
      this.playback = new AudioWorkletNode(ctx, 'playback-processor');
      this.playback.connect(ctx.destination);
      if (this.speakerId && typeof ctx.setSinkId === 'function') {
        try {
          await ctx.setSinkId(this.speakerId);
        } catch {
          // Output device selection is best-effort; fall back to the default sink.
        }
      }
    }

    // Uplink: mic -> capture worklet. Route it through a muted gain to the destination so the worklet
    // is pulled by the render graph without echoing the mic to the speaker. Skipped when the mic is off.
    if (micEnabled && this.stream) {
      const source = ctx.createMediaStreamSource(this.stream);
      this.capture = new AudioWorkletNode(ctx, 'capture-processor');
      this.capture.port.onmessage = (event) => this.onMicFrame(event.data as ArrayBuffer);
      const mute = new GainNode(ctx, { gain: 0 });
      source.connect(this.capture).connect(mute).connect(ctx.destination);
    }

    await ctx.resume();
    // If the autoplay policy kept it suspended (no gesture preceded creation), resume on first click.
    if (ctx.state !== 'running') {
      resumeOnFirstGesture(ctx);
    }
  }

  // Server-gated: the browser only streams mic audio while the simulated device is recording, matching
  // the WPF set/recording behavior.
  setRecording(on: boolean): void {
    this.recording = on;
  }

  private onMicFrame(pcm: ArrayBuffer): void {
    if (this.closed || !this.recording) return;
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;

    const frame = new Uint8Array(4 + pcm.byteLength);
    new DataView(frame.buffer).setInt32(0, this.seq++ & 0x7fffffff, false); // big-endian seq
    frame.set(new Uint8Array(pcm), 4);
    ws.send(frame);
  }

  private onDownlink(data: ArrayBuffer): void {
    if (this.closed || !this.playback) return;
    if (data.byteLength <= 4) return;

    // Skip the 4-byte sequence header; the rest is PCM16LE, which Int16Array reads natively.
    const samples = new Int16Array(data, 4);
    const floats = new Float32Array(samples.length);
    for (let i = 0; i < samples.length; i++) {
      floats[i] = samples[i] / 0x8000;
    }
    this.playback.port.postMessage(floats.buffer, [floats.buffer]);
  }

  async stop(): Promise<void> {
    this.closed = true;
    if (this.ctx) pendingResume.delete(this.ctx);
    try {
      this.ws?.close();
    } catch {
      // ignore
    }
    this.stream?.getTracks().forEach((track) => track.stop());
    try {
      await this.ctx?.close();
    } catch {
      // ignore
    }
  }
}

function waitForOpen(ws: WebSocket): Promise<void> {
  return new Promise((resolve, reject) => {
    ws.addEventListener('open', () => resolve(), { once: true });
    ws.addEventListener('error', () => reject(new Error('Audio WebSocket failed to open')), {
      once: true,
    });
  });
}
