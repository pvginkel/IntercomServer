import { useState } from 'react';
import { api } from '../api';
import type { RealDevice } from '../store';
import { Led } from './Led';
import { AudioConfigDialog } from './AudioConfigDialog';

const round2 = (value: number) => Math.round(value * 100) / 100;

export function RealDeviceCard({ device }: { device: RealDevice }) {
  const state = device.state;
  const offline = state?.online === false;
  const [busy, setBusy] = useState(false);
  const [drag, setDrag] = useState<number | null>(null);
  const [showConfig, setShowConfig] = useState(false);

  const name = device.config?.device?.name ?? device.id;
  const volume = drag ?? state?.volume ?? 0;

  const run = (action: () => Promise<unknown>) => {
    setBusy(true);
    action()
      .catch((error) => alert(String(error)))
      .finally(() => setBusy(false));
  };

  // Match the desktop slider: only push the value on release, not during the drag.
  const commitVolume = () => {
    if (drag === null) return;
    const value = round2(drag);
    setDrag(null);
    api.setVolume(device.id, value).catch((error) => alert(String(error)));
  };

  return (
    <div className={`card real${offline ? ' offline' : ''}`}>
      <div className="card-header">{device.id}</div>
      <div className="card-body">
        <div className="row">
          <span className="label">Name:</span>
          <span>{name}</span>
        </div>
        <div className="row">
          <span className="label">Volume:</span>
          <input
            type="range"
            min={0}
            max={1}
            step={0.01}
            value={volume}
            disabled={offline}
            onChange={(e) => setDrag(parseFloat(e.target.value))}
            onMouseUp={commitVolume}
            onTouchEnd={commitVolume}
            onKeyUp={commitVolume}
          />
          <span className="value">{Math.round(volume * 100)}%</span>
        </div>
        <div className="row leds">
          <Led on={!!state?.red_led} color="red" />
          <Led on={!!state?.green_led} color="green" />
        </div>
        <div className="row indicators">
          <label className="checkbox">
            <input
              type="checkbox"
              checked={!!state?.enabled}
              disabled={offline || busy}
              onChange={(e) => run(() => api.setEnabled(device.id, e.target.checked))}
            />
            Enabled
          </label>
          <label className="checkbox">
            <input type="checkbox" checked={!!state?.recording} readOnly disabled /> Recording
          </label>
          <label className="checkbox">
            <input type="checkbox" checked={!!state?.playing} readOnly disabled /> Playing
          </label>
        </div>
      </div>
      <div className="card-actions">
        <button
          disabled={offline || busy || !state?.audio_config}
          onClick={() => setShowConfig(true)}
        >
          Configure
        </button>
        <button disabled={offline || busy} onClick={() => run(() => api.identify(device.id))}>
          Identify
        </button>
        <button disabled={offline || busy} onClick={() => run(() => api.restart(device.id))}>
          Restart
        </button>
        <button disabled={busy} onClick={() => run(() => api.removeDevice(device.id))}>
          Remove
        </button>
      </div>
      {showConfig && state?.audio_config && (
        <AudioConfigDialog
          deviceId={device.id}
          initial={state.audio_config}
          onClose={() => setShowConfig(false)}
        />
      )}
    </div>
  );
}
