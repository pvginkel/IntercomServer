// Device DTOs mirror the C# wire contract (snake_case, the same shape that travels over MQTT).

export interface DeviceAudioFormat {
  channel_layout?: string;
  sample_rate?: number;
  bit_rate?: number;
}

export interface DeviceAudioFormats {
  in?: DeviceAudioFormat;
  out?: DeviceAudioFormat;
}

export interface DeviceDeviceConfiguration {
  manufacturer?: string;
  model?: string;
  name?: string;
  firmware_version?: string;
}

export interface DeviceConfiguration {
  unique_id?: string;
  audio_formats?: DeviceAudioFormats;
  device?: DeviceDeviceConfiguration;
  endpoint?: string;
}

export interface AudioConfiguration {
  volume_scale_low: number;
  volume_scale_high: number;
  enable_audio_processing: boolean;
  audio_buffer_ms: number;
  microphone_gain_bits: number;
  recording_auto_volume_enabled: boolean;
  recording_smoothing_factor: number;
  playback_auto_volume_enabled: boolean;
  playback_target_db: number;
}

export interface DeviceState {
  online?: boolean;
  enabled?: boolean;
  red_led?: boolean;
  green_led?: boolean;
  playing?: boolean;
  recording?: boolean;
  volume?: number;
  audio_config?: AudioConfiguration | null;
}

export type LedState = 'on' | 'off' | 'blink';

export interface DeviceLedAction {
  state: LedState;
  duration?: number;
  on?: number;
  off?: number;
}

export type DeviceKind = 'real' | 'sim';

// Server -> browser push messages on /ws/events.
export type WsMessage =
  | { type: 'device-config'; kind: DeviceKind; id: string; config: DeviceConfiguration }
  | {
      type: 'device-state';
      kind: DeviceKind;
      id: string;
      state: DeviceState;
      led_red?: DeviceLedAction;
      led_green?: DeviceLedAction;
    }
  | { type: 'device-removed'; kind: DeviceKind; id: string }
  | { type: 'server-settings'; auto_accept: boolean }
  | { type: 'aec-status'; state: string; has_sample: boolean };
