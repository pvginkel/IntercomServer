import { useState } from 'react';
import { api } from '../api';
import type { SimDevice } from '../store';
import { Led, useLedBlink } from './Led';

export function SimDeviceCard({ device }: { device: SimDevice }) {
  const [busy, setBusy] = useState(false);
  const state = device.state;
  const redOn = useLedBlink(device.ledRed);
  const greenOn = useLedBlink(device.ledGreen);

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
      </div>
      <div className="card-actions">
        <button disabled={busy} onClick={() => run(() => api.removeSimDevice(device.id))}>
          Remove
        </button>
      </div>
    </div>
  );
}
