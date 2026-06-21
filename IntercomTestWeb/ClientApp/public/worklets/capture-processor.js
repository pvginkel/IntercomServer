// Mic capture worklet. Runs inside an AudioContext({ sampleRate: 16000 }), so each render quantum is
// 128 Float32 samples already at 16 kHz (the browser resamples from hardware). It accumulates a 20 ms
// frame (320 samples), converts to PCM16LE, and posts the raw bytes to the main thread, which prepends
// the 4-byte big-endian sequence header and sends it over the audio WebSocket.
const FRAME_SAMPLES = 320; // 20 ms at 16 kHz

class CaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._frame = new Int16Array(FRAME_SAMPLES);
    this._count = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) {
      return true;
    }

    const channel = input[0]; // mono
    if (!channel) {
      return true;
    }

    for (let i = 0; i < channel.length; i++) {
      let sample = channel[i];
      if (sample > 1) sample = 1;
      else if (sample < -1) sample = -1;

      this._frame[this._count++] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;

      if (this._count === FRAME_SAMPLES) {
        const out = new Int16Array(this._frame); // copy so we can transfer it
        this.port.postMessage(out.buffer, [out.buffer]);
        this._count = 0;
      }
    }

    return true;
  }
}

registerProcessor('capture-processor', CaptureProcessor);
