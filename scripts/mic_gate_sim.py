#!/usr/bin/env python3
"""Simulate the ChatGPT mic noise gate over a recorded mic WAV.

This mirrors Conversation.EvaluateMicGate so we can tune the gate against real
recordings (the *_mic_16000.wav files written when CHATGPT_DEBUG_AUDIO_DIR is set)
without rebuilding/running the server. Tweak the thresholds here, find settings you
like, then port them back to the C# constants / env vars.

The live gate evaluates one variable-size UDP packet at a time; here we approximate
that by slicing the file into fixed --window-ms windows. Timings will therefore
differ from production by a few tens of ms, but the open/close structure matches.

Examples:
  # Open/close timeline with the current candidate settings
  python scripts/mic_gate_sim.py "tmp/debug_audio/..._mic_16000.wav"

  # Try different settings
  python scripts/mic_gate_sim.py FILE --threshold 1200 --attack-ms 80 --hold-ms 600

  # See the raw RMS envelope (per 0.5s bucket) to compare against an audio editor
  python scripts/mic_gate_sim.py FILE --envelope

  # Dump fine-grained RMS for a specific span (start end, seconds)
  python scripts/mic_gate_sim.py FILE --region 0 7
"""

import argparse
import math
import sys
import wave
from array import array


def read_pcm16_mono(path):
    """Return (samples: array('h'), sample_rate). Errors out on non-mono/16-bit."""
    with wave.open(path, "rb") as w:
        ch, sw, fr, n = (
            w.getnchannels(),
            w.getsampwidth(),
            w.getframerate(),
            w.getnframes(),
        )
        raw = w.readframes(n)
    if sw != 2:
        sys.exit(f"Expected 16-bit PCM, got sample width {sw} bytes.")
    samples = array("h")
    samples.frombytes(raw)
    if ch != 1:
        # De-interleave to mono by taking channel 0.
        samples = samples[0::ch]
    return samples, fr


def rms(samples, start, count):
    if count <= 0:
        return 0.0
    total = 0
    for i in range(start, start + count):
        v = samples[i]
        total += v * v
    return math.sqrt(total / count)


def dbfs(value):
    return float("-inf") if value <= 0 else 20 * math.log10(value / 32768.0)


def window_rms(samples, rate, window_ms):
    """RMS per fixed window. Returns list of (start_time_s, rms)."""
    wlen = max(1, int(rate * window_ms / 1000))
    out = []
    for i in range(0, len(samples) - wlen + 1, wlen):
        out.append((i / rate, rms(samples, i, wlen)))
    return out, wlen


def simulate_gate(env, rate, window_ms, threshold, attack_ms, hold_ms, preroll_ms):
    """Run the gate over the windowed RMS envelope.

    Mirrors EvaluateMicGate: a window is 'loud' when rms >= threshold. The gate
    opens once it has seen >= attack_ms of continuous loud windows, and stays open
    until hold_ms has elapsed since the last loud window. attack_ms=0 reproduces
    the current single-packet C# behaviour (open on the first loud window).

    preroll_ms controls the reported open time (what a look-back ring buffer would
    flush when the gate opens):
      * None  -> open at the moment the attack requirement is met (no look-back;
                 what flows live today).
      * >= 0  -> open at the detected onset (first loud window) minus preroll_ms,
                 i.e. include preroll_ms of audio before speech was detected.
                 Clamped so it never backs into the previous interval or below 0.
    The real ring buffer must hold attack_ms + preroll_ms of audio for this.
    """
    win_s = window_ms / 1000
    hold_s = hold_ms / 1000
    preroll_s = None if preroll_ms is None else preroll_ms / 1000
    attack_windows = max(1, round(attack_ms / window_ms))

    intervals = []
    consec_loud = 0
    is_open = False
    open_time = None
    run_start_time = None
    last_loud_time = None
    prev_close = 0.0

    for t, value in env:
        loud = value >= threshold
        if loud:
            if consec_loud == 0:
                run_start_time = t
            consec_loud += 1
            last_loud_time = t
        else:
            consec_loud = 0

        if not is_open:
            if consec_loud >= attack_windows:
                is_open = True
                if preroll_s is None:
                    open_time = t
                else:
                    open_time = max(prev_close, run_start_time - preroll_s, 0.0)
        elif not loud and (t - last_loud_time) > hold_s:
            is_open = False
            # The gate held for hold_s past the final loud window (plus its own length).
            close_time = last_loud_time + win_s + hold_s
            intervals.append((open_time, close_time))
            prev_close = close_time
            open_time = None

    if is_open:
        intervals.append((open_time, env[-1][0] + win_s))
    return intervals


def parse_labels(values):
    """Parse 'start-end' strings (seconds) into sorted (start, end) tuples."""
    labels = []
    for v in values:
        a, b = v.split("-")
        labels.append((float(a), float(b)))
    return sorted(labels)


def score_against_labels(intervals, labels):
    """Compare gate intervals to ground-truth speech labels.

    Prints, per labelled utterance: the overlapping gate interval, the onset clip
    (gate opened this much after speech started — positive clips the start) and the
    tail margin (gate stayed open this much past speech end — negative clips the
    tail). Then summarises missed speech and audio forwarded outside any utterance.
    """
    print("\nScored against ground-truth speech labels:")
    print("  speech start-end       gate open-close        onset clip    tail margin")
    for ls, le in labels:
        overlap = [(o, c) for (o, c) in intervals if c > ls and o < le]
        if not overlap:
            print(f"  {ls:6.2f}-{le:6.2f}s       (gate never opened)        MISSED")
            continue
        o = min(x[0] for x in overlap)
        c = max(x[1] for x in overlap)
        onset = o - ls           # >0 = opened late, clipping the onset
        tail = c - le            # >0 = stayed open past end; <0 = cut the tail
        flags = []
        if onset > 0.02:
            flags.append(f"clips {onset*1000:.0f}ms onset")
        if tail < 0:
            flags.append(f"CUTS {(-tail)*1000:.0f}ms tail")
        note = ("  <- " + ", ".join(flags)) if flags else ""
        print(f"  {ls:6.2f}-{le:6.2f}s       {o:6.2f}-{c:6.2f}s       "
              f"{onset*1000:+6.0f}ms      {tail*1000:+7.0f}ms{note}")

    # Audio forwarded that does not overlap any labelled speech = leaked non-speech.
    leaked = 0.0
    for o, c in intervals:
        cur = o
        for ls, le in labels:
            if le <= cur or ls >= c:
                continue
            if ls > cur:
                leaked += ls - cur
            cur = max(cur, le)
        if c > cur:
            leaked += c - cur
    speech_total = sum(le - ls for ls, le in labels)
    open_total = sum(c - o for o, c in intervals)
    print(f"\n  Speech labelled: {speech_total:.1f}s   gate open: {open_total:.1f}s   "
          f"forwarded outside speech: {leaked:.1f}s")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("wav", help="Path to a *_mic_16000.wav recording.")
    p.add_argument("--threshold", type=float, default=1500, help="RMS open threshold (default 1500).")
    p.add_argument("--attack-ms", type=float, default=60, help="Continuous loud audio required to open (default 60; 0 = current single-packet gate).")
    p.add_argument("--hold-ms", type=float, default=700, help="Stay open this long after the last loud window (default 700).")
    p.add_argument("--window-ms", type=float, default=20, help="RMS window size, proxy for a UDP packet (default 20).")
    p.add_argument("--lookback", action="store_true", help="Open at the detected onset (first loud window) instead of after the attack delay; equivalent to --preroll-ms 0.")
    p.add_argument("--preroll-ms", type=float, default=80, help="Include this much audio before the detected onset when opening (look-back). Default 80 matches the shipped gate; pass a negative value to disable look-back entirely.")
    p.add_argument("--envelope", action="store_true", help="Print the RMS envelope per 0.5s bucket.")
    p.add_argument("--region", nargs=2, type=float, metavar=("START", "END"), help="Print fine-grained per-window RMS for a time span (seconds).")
    p.add_argument("--labels", nargs="+", metavar="START-END", help="Ground-truth speech spans (e.g. 0.274-2.586) to score the gate against.")
    args = p.parse_args()

    samples, rate = read_pcm16_mono(args.wav)
    duration = len(samples) / rate
    env, wlen = window_rms(samples, rate, args.window_ms)

    print(f"File: {args.wav}")
    print(f"  {rate} Hz mono, {duration:.2f} s, {len(samples)} samples")
    # Negative pre-roll disables look-back entirely; --lookback forces onset-aligned (pre-roll 0).
    preroll_ms = None if args.preroll_ms < 0 else args.preroll_ms
    if args.lookback and args.preroll_ms < 0:
        preroll_ms = 0.0
    lookback_desc = "off" if preroll_ms is None else f"onset-{preroll_ms:g}ms"
    print(f"Config: threshold={args.threshold:g}  attack={args.attack_ms:g}ms  "
          f"hold={args.hold_ms:g}ms  window={args.window_ms:g}ms  look-back={lookback_desc}")

    if args.region:
        a, b = args.region
        print(f"\nPer-window RMS, {a:.3f}-{b:.3f}s:")
        rowvals = [(t, v) for (t, v) in env if a <= t <= b]
        for k in range(0, len(rowvals), 8):
            print("  " + "  ".join(f"{t:6.3f}:{v:5.0f}" for t, v in rowvals[k:k + 8]))

    if args.envelope:
        print("\nEnvelope (max & avg RMS per 0.5s):")
        per = max(1, int(0.5 / (args.window_ms / 1000)))
        gmax = max((v for _, v in env), default=1) or 1
        for b in range(0, len(env), per):
            seg = [v for _, v in env[b:b + per]]
            m, avg = max(seg), sum(seg) / len(seg)
            bar = "#" * int(40 * m / gmax)
            print(f"  {env[b][0]:6.2f}s  max={m:6.0f}  avg={avg:6.0f} |{bar}")

    intervals = simulate_gate(env, rate, args.window_ms, args.threshold,
                              args.attack_ms, args.hold_ms, preroll_ms)

    print(f"\nGate opens the stream {len(intervals)} time(s):")
    print("   #   open       close      duration")
    total = 0.0
    for i, (o, c) in enumerate(intervals, 1):
        total += c - o
        print(f"  {i:2d}.  {o:7.2f}s   {c:7.2f}s   {c - o:6.2f}s")
    pct = 100 * total / duration if duration else 0
    print(f"\nOpen {total:.1f}s of {duration:.1f}s ({pct:.0f}%); muted the rest.")

    if args.labels:
        score_against_labels(intervals, parse_labels(args.labels))


if __name__ == "__main__":
    main()
