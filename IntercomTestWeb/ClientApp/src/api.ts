import type { AudioConfiguration } from './types';

async function send(method: string, path: string, body?: unknown): Promise<void> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    const text = await response.text().catch(() => '');
    throw new Error(`${response.status} ${response.statusText}${text ? `: ${text}` : ''}`);
  }
}

const id = (value: string) => encodeURIComponent(value);

export const api = {
  setVolume: (device: string, volume: number) =>
    send('POST', `/devices/${id(device)}/volume`, { volume }),
  setEnabled: (device: string, enabled: boolean) =>
    send('POST', `/devices/${id(device)}/enabled`, { enabled }),
  identify: (device: string) => send('POST', `/devices/${id(device)}/identify`),
  restart: (device: string) => send('POST', `/devices/${id(device)}/restart`),
  setAudioConfig: (device: string, config: AudioConfiguration) =>
    send('POST', `/devices/${id(device)}/audio-config`, config),
  removeDevice: (device: string) => send('DELETE', `/devices/${id(device)}`),

  addSimDevice: () => send('POST', '/sim-devices'),
  removeSimDevice: (device: string) => send('DELETE', `/sim-devices/${id(device)}`),
  simAction: (device: string, action: 'click' | 'long_click') =>
    send('POST', `/sim-devices/${id(device)}/action`, { action }),

  doorbell: () => send('POST', '/server/doorbell'),
  setAutoAccept: (enabled: boolean) => send('POST', '/server/auto-accept', { enabled }),
};
