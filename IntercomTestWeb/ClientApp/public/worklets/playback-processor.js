// Speaker playback worklet. The main thread posts Float32 PCM chunks (decoded from the downlink WS
// frames) and this worklet feeds them to the AudioContext({ sampleRate: 16000 }) output, which the
// browser resamples to hardware. A small ring of pending chunks (~150 ms target) absorbs WebSocket
// jitter on the way down; on underrun it rebuffers before resuming so playback stays smooth rather
// than crackling.
const PREBUFFER_SAMPLES = 2400; // ~150 ms at 16 kHz

class PlaybackProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._queue = []; // Float32Array chunks waiting to play
    this._readOffset = 0; // sample offset into _queue[0]
    this._queued = 0; // total samples across the queue
    this._playing = false; // false until we have buffered PREBUFFER_SAMPLES

    this.port.onmessage = (event) => {
      const chunk = new Float32Array(event.data);
      this._queue.push(chunk);
      this._queued += chunk.length;
    };
  }

  process(_inputs, outputs) {
    const out = outputs[0][0];
    if (!out) {
      return true;
    }

    // Wait until enough is buffered before (re)starting, so jitter doesn't immediately underrun us.
    if (!this._playing) {
      if (this._queued >= PREBUFFER_SAMPLES) {
        this._playing = true;
      } else {
        out.fill(0);
        return true;
      }
    }

    for (let i = 0; i < out.length; i++) {
      if (this._queue.length === 0) {
        out[i] = 0;
        this._playing = false; // underrun -> rebuffer
        continue;
      }

      const head = this._queue[0];
      out[i] = head[this._readOffset++];
      this._queued--;

      if (this._readOffset >= head.length) {
        this._queue.shift();
        this._readOffset = 0;
      }
    }

    return true;
  }
}

registerProcessor('playback-processor', PlaybackProcessor);
