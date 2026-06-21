import { createContext, useContext, useState, type ReactNode } from 'react';

// A single full-screen gate shown on load. The browser requires a user gesture to grant microphone
// access and to start an AudioContext, so this turns that unavoidable one-time click into an app-wide
// step: accept once and audio is forced on for every simulated device (no per-card toggle). The grant
// persists for the origin, so per-device sessions never re-prompt, and the click gives the page the
// sticky activation each device's AudioContext needs to resume.
//
// useAudioReady() is true once permission was granted. "Continue without audio" lets the operator into
// the rest of the tool if they deny the prompt or run over plain HTTP (where getUserMedia is blocked);
// in that case audio stays off rather than bricking the whole UI.

const AudioReadyContext = createContext(false);

export const useAudioReady = () => useContext(AudioReadyContext);

type Phase = 'gate' | 'granted' | 'skipped';

export function AudioGate({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<Phase>('gate');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const enable = async () => {
    setBusy(true);
    setError(null);
    try {
      if (!navigator.mediaDevices?.getUserMedia) {
        throw new Error('Microphone access requires HTTPS or localhost.');
      }
      // Prompt once; we only need the grant + the gesture, so release the stream immediately. Each
      // device opens its own stream later with its selected mic.
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      stream.getTracks().forEach((track) => track.stop());
      setPhase('granted');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  if (phase !== 'gate') {
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
        <p>
          This tool bridges simulated-device audio through your browser, so it needs microphone and
          speaker access. Grant it once to continue.
        </p>
        <button className="primary" disabled={busy} onClick={enable}>
          {busy ? 'Requesting…' : 'Enable audio & continue'}
        </button>
        {error && <p className="gate-error">{error}</p>}
        {error && (
          <button className="gate-skip" onClick={() => setPhase('skipped')}>
            Continue without audio
          </button>
        )}
      </div>
    </div>
  );
}
