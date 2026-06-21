import { useSyncExternalStore } from 'react';
import type { DeviceConfiguration, DeviceState, DeviceLedAction, WsMessage } from './types';

export interface RealDevice {
  id: string;
  config?: DeviceConfiguration;
  state?: DeviceState;
}

export interface SimDevice {
  id: string;
  config?: DeviceConfiguration;
  state?: DeviceState;
  ledRed?: DeviceLedAction;
  ledGreen?: DeviceLedAction;
}

interface StoreState {
  connected: boolean;
  autoAccept: boolean;
  real: Record<string, RealDevice>;
  sim: Record<string, SimDevice>;
}

let state: StoreState = { connected: false, autoAccept: false, real: {}, sim: {} };
const listeners = new Set<() => void>();

function set(next: StoreState) {
  state = next;
  for (const listener of listeners) listener();
}

function without<T>(map: Record<string, T>, key: string): Record<string, T> {
  const next = { ...map };
  delete next[key];
  return next;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function useStore(): StoreState {
  return useSyncExternalStore(subscribe, () => state);
}

export function setConnected(connected: boolean) {
  set({ ...state, connected });
}

export function applyMessage(message: WsMessage) {
  switch (message.type) {
    case 'server-settings':
      set({ ...state, autoAccept: message.auto_accept });
      break;

    case 'device-config':
      if (message.kind === 'real') {
        const current = state.real[message.id] ?? { id: message.id };
        set({ ...state, real: { ...state.real, [message.id]: { ...current, config: message.config } } });
      } else {
        const current = state.sim[message.id] ?? { id: message.id };
        set({ ...state, sim: { ...state.sim, [message.id]: { ...current, config: message.config } } });
      }
      break;

    case 'device-state':
      if (message.kind === 'real') {
        const current = state.real[message.id] ?? { id: message.id };
        set({ ...state, real: { ...state.real, [message.id]: { ...current, state: message.state } } });
      } else {
        const current = state.sim[message.id] ?? { id: message.id };
        set({
          ...state,
          sim: {
            ...state.sim,
            [message.id]: {
              ...current,
              state: message.state,
              ledRed: message.led_red,
              ledGreen: message.led_green,
            },
          },
        });
      }
      break;

    case 'device-removed':
      if (message.kind === 'real') {
        set({ ...state, real: without(state.real, message.id) });
      } else {
        set({ ...state, sim: without(state.sim, message.id) });
      }
      break;

    case 'aec-status':
      // Reserved for Phase C (the AEC view); ignored for now.
      break;
  }
}
