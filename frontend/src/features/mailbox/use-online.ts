import { useEffect, useState, useSyncExternalStore } from 'react';
import { useRealtimeStatus } from '@/features/realtime';

function subscribe(onChange: () => void) {
  window.addEventListener('online', onChange);
  window.addEventListener('offline', onChange);
  return () => {
    window.removeEventListener('online', onChange);
    window.removeEventListener('offline', onChange);
  };
}

/** How long the live channel must stay down before the notice is shown (a blink, such as waking a laptop, is not worth one). */
export const CONNECTION_GRACE_MS = 3000;

/**
 * Whether the connection to the server is lost as far as the screen can tell: the live channel (new mail, counters) has been
 * trying to come back for longer than a moment. The browser's own "offline" is separate (`useOnline`), as it says nothing about a
 * server that cannot be reached. Goes away at once when the channel is back.
 */
export function useConnectionLost(graceMs: number = CONNECTION_GRACE_MS): boolean {
  const down = useRealtimeStatus() === 'reconnecting';
  const [lost, setLost] = useState(false);
  useEffect(() => {
    if (!down) return;
    const timer = setTimeout(() => setLost(true), graceMs);
    return () => {
      clearTimeout(timer);
      setLost(false); // the notice goes with the cause, and the next loss gets its own moment of grace
    };
  }, [down, graceMs]);
  return down && lost;
}

/** Whether the browser thinks it has a network; follows the browser's online and offline events. */
export function useOnline(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => navigator.onLine,
    () => true,
  );
}
