import { useState } from 'react';
import { api } from '../api';
import { useStore } from '../store';

export function Toolbar() {
  const { connected, autoAccept } = useStore();
  const [busy, setBusy] = useState(false);

  const run = (action: () => Promise<unknown>) => {
    setBusy(true);
    action()
      .catch((error) => alert(String(error)))
      .finally(() => setBusy(false));
  };

  return (
    <div className="toolbar">
      <button disabled={busy} onClick={() => run(() => api.addSimDevice())}>
        Add Device
      </button>
      <button disabled={busy} onClick={() => run(() => api.doorbell())}>
        Doorbell
      </button>
      <label className="checkbox">
        <input
          type="checkbox"
          checked={autoAccept}
          disabled={busy}
          onChange={(e) => run(() => api.setAutoAccept(e.target.checked))}
        />
        Auto Accept
      </label>
      <button disabled title="Available in Phase C">
        AEC Test
      </button>
      <span className="spacer" />
      <span className={`status ${connected ? 'ok' : 'bad'}`}>
        {connected ? 'connected' : 'disconnected'}
      </span>
    </div>
  );
}
