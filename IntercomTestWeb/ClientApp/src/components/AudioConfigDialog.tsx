import { useMemo, useState } from 'react';
import { api } from '../api';
import type { AudioConfiguration } from '../types';

type FieldKind = 'double' | 'int' | 'bool';

interface Field {
  key: keyof AudioConfiguration;
  label: string;
  kind: FieldKind;
}

const FIELDS: Field[] = [
  { key: 'volume_scale_low', label: 'Volume scale start (low end)', kind: 'double' },
  { key: 'volume_scale_high', label: 'Volume scale end (high end)', kind: 'double' },
  { key: 'enable_audio_processing', label: 'Enable audio processing', kind: 'bool' },
  { key: 'audio_buffer_ms', label: 'Audio buffer (ms)', kind: 'int' },
  { key: 'microphone_gain_bits', label: 'Microphone gain bits', kind: 'int' },
  { key: 'recording_auto_volume_enabled', label: 'Recording auto volume enabled', kind: 'bool' },
  { key: 'recording_smoothing_factor', label: 'Recording smoothing factor', kind: 'double' },
  { key: 'playback_auto_volume_enabled', label: 'Playback auto volume enabled', kind: 'bool' },
  { key: 'playback_target_db', label: 'Playback target dB', kind: 'double' },
];

type FormValues = Record<string, string | boolean>;

function toForm(config: Partial<AudioConfiguration>): FormValues {
  const values: FormValues = {};
  for (const field of FIELDS) {
    const value = config[field.key];
    values[field.key] = field.kind === 'bool' ? !!value : value === undefined ? '' : String(value);
  }
  return values;
}

// Returns the parsed configuration, or null when any numeric field is empty/invalid (mirrors the
// WPF dialog disabling OK/Copy until every field parses).
function parseForm(values: FormValues): AudioConfiguration | null {
  const result: Record<string, number | boolean> = {};
  for (const field of FIELDS) {
    const value = values[field.key];
    if (field.kind === 'bool') {
      result[field.key] = !!value;
      continue;
    }
    const text = String(value).trim();
    const parsed = Number(text);
    if (text === '' || !Number.isFinite(parsed)) return null;
    if (field.kind === 'int' && !Number.isInteger(parsed)) return null;
    result[field.key] = parsed;
  }
  return result as unknown as AudioConfiguration;
}

export function AudioConfigDialog({
  deviceId,
  initial,
  onClose,
}: {
  deviceId: string;
  initial: AudioConfiguration;
  onClose: () => void;
}) {
  const [values, setValues] = useState<FormValues>(() => toForm(initial));
  const [busy, setBusy] = useState(false);
  const parsed = useMemo(() => parseForm(values), [values]);

  const setField = (key: string, value: string | boolean) =>
    setValues((prev) => ({ ...prev, [key]: value }));

  const onOk = () => {
    if (!parsed) return;
    setBusy(true);
    api
      .setAudioConfig(deviceId, parsed)
      .then(onClose)
      .catch((error) => {
        alert(String(error));
        setBusy(false);
      });
  };

  const onCopy = () => {
    if (parsed) navigator.clipboard.writeText(JSON.stringify(parsed, null, 2)).catch(() => {});
  };

  const onPaste = async () => {
    try {
      const text = await navigator.clipboard.readText();
      setValues(toForm(JSON.parse(text)));
    } catch (error) {
      alert(`Failed to paste: ${error}`);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h2>Audio Configuration</h2>
        <div className="form">
          {FIELDS.map((field) => (
            <div className="form-row" key={field.key}>
              <label>{field.label}</label>
              {field.kind === 'bool' ? (
                <input
                  type="checkbox"
                  checked={!!values[field.key]}
                  onChange={(e) => setField(field.key, e.target.checked)}
                />
              ) : (
                <input
                  type="text"
                  inputMode="decimal"
                  value={String(values[field.key])}
                  onChange={(e) => setField(field.key, e.target.value)}
                />
              )}
            </div>
          ))}
        </div>
        <div className="modal-actions">
          <button onClick={onCopy} disabled={!parsed}>
            Copy
          </button>
          <button onClick={onPaste}>Paste</button>
          <span className="spacer" />
          <button className="primary" onClick={onOk} disabled={!parsed || busy}>
            OK
          </button>
          <button onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
