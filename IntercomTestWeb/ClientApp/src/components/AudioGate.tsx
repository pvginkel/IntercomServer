import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

// A single full-screen gate shown on load. It requests microphone access automatically (Chrome — the
// target browser — prompts without a user gesture), so the operator just accepts the prompt and the
// whole app's audio is forced on: every simulated device auto-starts, with no per-card toggle. The
// grant persists for the origin, so per-device sessions never re-prompt.
//
// useAudioReady() is true once permission was granted. If the request fails (denied, or plain HTTP to
// a non-localhost host where getUserMedia is blocked — and Safari, which needs a gesture), the gate
// offers Retry plus "Continue without audio" so the rest of the tool stays usable rather than bricking.
//
// Note on suspension: a context created off the back of an auto-requested (gesture-less) grant can
// start suspended under the autoplay policy; SimAudioSession resumes it on the first page interaction.

const AudioReadyContext = createContext(false);

export const useAudioReady = () => useContext(AudioReadyContext);

type Phase = 'requesting' | 'granted' | 'error' | 'skipped';

export function AudioGate({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<Phase>('requesting');
  const [error, setError] = useState<string | null>(null);
  const requested = useRef(false);

  const request = async () => {
    setPhase('requesting');
    setError(null);
    try {
      if (!navigator.mediaDevices?.getUserMedia) {
        throw new Error('Microphone access requires HTTPS or localhost.');
      }
      // We only need the grant; release the probe stream immediately. Each device opens its own
      // stream later with its selected mic.
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      stream.getTracks().forEach((track) => track.stop());
      setPhase('granted');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setPhase('error');
    }
  };

  useEffect(() => {
    // Guard against React StrictMode's double-invoke so the prompt is only requested once.
    if (requested.current) return;
    requested.current = true;
    void request();
  }, []);

  if (phase === 'granted' || phase === 'skipped') {
    return (
      <AudioReadyContext.Provider value={phase === 'granted'}>
        {children}
      </AudioReadyContext.Provider>
    );
  }

  return (
    <div className="gate">
      <div className="gate-box">
        <h1>IntercomTest</h1>
        {phase === 'requesting' ? (
          <p>Requesting microphone access…</p>
        ) : (
          <>
            <p>
              This tool bridges simulated-device audio through your browser, so it needs microphone
              and speaker access.
            </p>
            <p className="gate-error">{error}</p>
            <button className="primary" onClick={() => void request()}>
              Retry
            </button>
            <button className="gate-skip" onClick={() => setPhase('skipped')}>
              Continue without audio
            </button>
          </>
        )}
      </div>
    </div>
  );
}
