import { useState } from 'react';
import { api } from '../api';
import type { SimDevice } from '../store';
import { Led, useLedBlink } from './Led';
import { useSimAudio } from '../useSimAudio';
import { useAudioReady } from './AudioGate';

export function SimDeviceCard({ device }: { device: SimDevice }) {
  const [busy, setBusy] = useState(false);
  const state = device.state;
  const redOn = useLedBlink(device.ledRed);
  const greenOn = useLedBlink(device.ledGreen);

  const audioReady = useAudioReady();
  const audio = useSimAudio(device.id, !!state?.recording, audioReady);

  const run = (action: () => Promise<unknown>) => {
    setBusy(true);
    action()
      .catch((error) => alert(String(error)))
      .finally(() => setBusy(false));
  };

  return (
    <div className="card sim">
      <div className="card-header">
        {device.id}
        <span className="badge">simulated</span>
      </div>
      <div className="card-body">
        <div className="row buttons">
          <button disabled={busy} onClick={() => run(() => api.simAction(device.id, 'click'))}>
            Click
          </button>
          <button
            disabled={busy}
            onClick={() => run(() => api.simAction(device.id, 'long_click'))}
          >
            Long Click
          </button>
        </div>
        <div className="row leds">
          <Led on={redOn} color="red" />
          <Led on={greenOn} color="green" />
        </div>
        <div className="row indicators">
          <label className="checkbox">
            <input type="checkbox" checked={!!state?.recording} readOnly disabled /> Recording
          </label>
          <label className="checkbox">
            <input type="checkbox" checked={!!state?.playing} readOnly disabled /> Playing
          </label>
        </div>

        <div className="row">
          <span className="label">Mic</span>
          <DevicePicker
            kind="mic"
            devices={audio.devices.mics}
            value={audio.micId}
            onChange={audio.setMicId}
          />
        </div>
        <div className="row">
          <span className="label">Speaker</span>
          <DevicePicker
            kind="speaker"
            devices={audio.devices.speakers}
            value={audio.speakerId}
            onChange={audio.setSpeakerId}
          />
        </div>
        <div className="row">
          <span className="label">Audio</span>
          {audio.error ? (
            <span className="status bad">{audio.error}</span>
          ) : audioReady ? (
            <span className="status ok">live · {state?.recording ? 'mic streaming' : 'mic idle'}</span>
          ) : (
            <span className="status">off</span>
          )}
        </div>
      </div>
      <div className="card-actions">
        <button disabled={busy} onClick={() => run(() => api.removeSimDevice(device.id))}>
          Remove
        </button>
      </div>
    </div>
  );
}

function DevicePicker({
  kind,
  devices,
  value,
  onChange,
}: {
  kind: 'mic' | 'speaker';
  devices: MediaDeviceInfo[];
  value: string;
  onChange: (id: string) => void;
}) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Default</option>
      {devices.map((d, i) => (
        <option key={d.deviceId} value={d.deviceId}>
          {d.label || `${kind === 'mic' ? 'Microphone' : 'Speaker'} ${i + 1}`}
        </option>
      ))}
    </select>
  );
}
