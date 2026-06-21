import { useEffect, useState } from 'react';
import type { DeviceLedAction } from '../types';

export function Led({ on, color }: { on: boolean; color: string }) {
  return (
    <span
      className="led"
      style={{ background: on ? color : 'transparent', borderColor: color }}
    />
  );
}

// Derives the lit/unlit state from a raw LED action, animating the blink case. Used by the
// simulated-device card (the original WPF IntercomClientControl animated its LEDs; the real-device
// control only showed a static on/off, which is what <Led on=.../> covers directly).
export function useLedBlink(action?: DeviceLedAction): boolean {
  const [on, setOn] = useState(false);

  useEffect(() => {
    if (!action || action.state === 'off') {
      setOn(false);
      return;
    }

    if (action.state === 'on') {
      setOn(true);
      return;
    }

    // Blink: alternate using the on/off durations.
    let timer = 0;
    let lit = false;
    const onMs = action.on ?? 250;
    const offMs = action.off ?? 250;

    const tick = () => {
      lit = !lit;
      setOn(lit);
      timer = window.setTimeout(tick, lit ? onMs : offMs);
    };

    tick();

    return () => window.clearTimeout(timer);
  }, [action?.state, action?.on, action?.off]);

  return on;
}
