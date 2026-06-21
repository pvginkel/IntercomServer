import { useEffect, useRef, useState } from 'react';
import { SimAudioSession, listAudioDevices, AUDIO_OFF, type AudioDevices } from './audio';

// React lifecycle wrapper around a SimAudioSession. Audio is enabled globally by the AudioGate (one
// permission prompt for the whole app), so this auto-starts a session whenever `active` is true —
// there is no per-card toggle. The `recording` flag (driven by the device's MQTT state) gates whether
// mic audio is actually streamed; changing the mic or speaker selection restarts the session.
export interface SimAudio {
  devices: AudioDevices;
  micId: string;
  setMicId: (id: string) => void;
  speakerId: string;
  setSpeakerId: (id: string) => void;
  error: string | null;
}

export function useSimAudio(deviceId: string, recording: boolean, active: boolean): SimAudio {
  const [devices, setDevices] = useState<AudioDevices>({ mics: [], speakers: [] });
  // Channels start off; the operator selects a device to enable each one.
  const [micId, setMicId] = useState(AUDIO_OFF);
  const [speakerId, setSpeakerId] = useState(AUDIO_OFF);
  const [error, setError] = useState<string | null>(null);

  const sessionRef = useRef<SimAudioSession | null>(null);
  const recordingRef = useRef(recording);

  // Enumerate audio devices once audio is enabled (labels are only populated after the permission
  // grant) and whenever the system set changes.
  useEffect(() => {
    if (!active) return;

    const media = navigator.mediaDevices;
    if (!media) return;

    let alive = true;
    const refresh = () =>
      listAudioDevices()
        .then((d) => alive && setDevices(d))
        .catch(() => {});

    refresh();
    media.addEventListener('devicechange', refresh);
    return () => {
      alive = false;
      media.removeEventListener('devicechange', refresh);
    };
  }, [active]);

  // Push the recording flag to the live session without restarting it.
  useEffect(() => {
    recordingRef.current = recording;
    sessionRef.current?.setRecording(recording);
  }, [recording]);

  // Auto start / restart / stop the session. Restarts when the selected mic or speaker changes.
  useEffect(() => {
    if (!active) return;

    let cancelled = false;
    const session = new SimAudioSession(deviceId, micId, speakerId);
    sessionRef.current = session;
    session.setRecording(recordingRef.current);
    setError(null);

    session
      .start()
      .catch((e) => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : String(e));
      });

    return () => {
      cancelled = true;
      sessionRef.current = null;
      void session.stop();
    };
  }, [active, micId, speakerId, deviceId]);

  return { devices, micId, setMicId, speakerId, setSpeakerId, error };
}
