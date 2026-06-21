import { applyMessage, setConnected } from './store';
import type { WsMessage } from './types';

// Opens the /ws/events JSON push channel and keeps it open, reconnecting after a drop. The server
// replays the full current state on connect, so the store is rebuilt from scratch each time.
export function connectEvents() {
  const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
  const url = `${protocol}://${location.host}/ws/events`;

  const connect = () => {
    const socket = new WebSocket(url);

    socket.onopen = () => setConnected(true);

    socket.onmessage = (event) => {
      try {
        applyMessage(JSON.parse(event.data) as WsMessage);
      } catch (error) {
        console.error('Failed to handle event message', error);
      }
    };

    socket.onclose = () => {
      setConnected(false);
      setTimeout(connect, 1500);
    };

    socket.onerror = () => socket.close();
  };

  connect();
}
