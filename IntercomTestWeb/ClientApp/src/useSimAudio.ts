import { useEffect, useRef, useState } from 'react';
import { SimAudioSession, listAudioDevices, type AudioDevices } from './audio';

// React lifecycle wrapper around a SimAudioSession. Enabling opens the audio WebSocket + AudioContext
// and acquires the mic; the `recording` flag (driven by the device's MQTT state) gates whether mic
// audio is actually streamed. Changing the mic or speaker selection while enabled restarts the
// session with the new devices.
export interface SimAudio {
  enabled: boolean;
  setEnabled: (on: boolean) => void;
  devices: AudioDevices;
  micId: string;
  setMicId: (id: string) => void;
  speakerId: string;
  setSpeakerId: (id: string) => void;
  error: string | null;
}

export function useSimAudio(deviceId: string, recording: boolean): SimAudio {
  const [enabled, setEnabled] = useState(false);
  const [devices, setDevices] = useState<AudioDevices>({ mics: [], speakers: [] });
  const [micId, setMicId] = useState('');
  const [speakerId, setSpeakerId] = useState('');
  const [error, setError] = useState<string | null>(null);

  const sessionRef = useRef<SimAudioSession | null>(null);
  const recordingRef = useRef(recording);

  // Enumerate audio devices up front and whenever the system set changes. Labels stay blank until the
  // first getUserMedia grant, so we re-enumerate after the session starts too (below).
  useEffect(() => {
    const media = navigator.mediaDevices;
    if (!media) return; // Insecure context (plain HTTP to a non-localhost host): no device access.

    let active = true;
    const refresh = () =>
      listAudioDevices()
        .then((d) => active && setDevices(d))
        .catch(() => {});

    refresh();
    media.addEventListener('devicechange', refresh);
    return () => {
      active = false;
      media.removeEventListener('devicechange', refresh);
    };
  }, []);

  // Push the recording flag to the live session without restarting it.
  useEffect(() => {
    recordingRef.current = recording;
    sessionRef.current?.setRecording(recording);
  }, [recording]);

  // Start / restart / stop the session. Restarts when the selected mic or speaker changes.
  useEffect(() => {
    if (!enabled) return;

    let cancelled = false;
    const session = new SimAudioSession(deviceId, micId, speakerId);
    sessionRef.current = session;
    session.setRecording(recordingRef.current);
    setError(null);

    session
      .start()
      .then(() => {
        if (cancelled) return;
        // Device labels are available now that mic permission was granted.
        listAudioDevices()
          .then(setDevices)
          .catch(() => {});
      })
      .catch((e) => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : String(e));
        setEnabled(false);
      });

    return () => {
      cancelled = true;
      sessionRef.current = null;
      void session.stop();
    };
  }, [enabled, micId, speakerId, deviceId]);

  return {
    enabled,
    setEnabled,
    devices,
    micId,
    setMicId,
    speakerId,
    setSpeakerId,
    error,
  };
}
