import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { applyMailEvent } from './apply-event';
import {
  createHubConnection,
  HubConnectionState,
  isRefusal,
  retryDelay,
  type HubLike,
  type RealtimeStatus,
} from './connection';
import { asMailEvent, EVENT_METHOD } from './events';
import { RealtimeContext } from './realtime-context';
import { FOLDERS_KEY } from '@/features/mailbox/api';

export interface RealtimeProviderProps {
  children: ReactNode;
  /** Makes the connection; tests pass a stand-in. */
  connect?: () => HubLike;
}

/**
 * Keeps the real-time channel open while it is mounted (the signed-in screens), and puts what it brings into the caches of
 * TanStack Query (`applyMailEvent`), so that new mail, marks and counters appear without anyone asking. The first connection
 * is tried again until it succeeds; after a lost connection the library tries again with growing pauses, and when the channel
 * is back everything that may have been missed is read again. A refused session ends the attempts (the next request signs the
 * user out). Nothing of this is shown yet except the status (`useRealtimeStatus`).
 */
export function RealtimeProvider({
  children,
  connect = createHubConnection,
}: RealtimeProviderProps) {
  const queryClient = useQueryClient();
  // The connection is made once per mount; where none can be made the screens work without it, on what they ask for.
  const [hub] = useState<HubLike | null>(() => {
    try {
      return connect();
    } catch {
      return null;
    }
  });
  const [status, setStatus] = useState<RealtimeStatus>(hub ? 'connecting' : 'disconnected');

  useEffect(() => {
    if (!hub) return;
    let wanted = true;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const resync = () => {
      void queryClient.invalidateQueries({ queryKey: ['messages'] });
      void queryClient.invalidateQueries({ queryKey: ['message'] });
      void queryClient.invalidateQueries({ queryKey: FOLDERS_KEY });
    };

    hub.on(EVENT_METHOD, (value: unknown) => {
      const event = asMailEvent(value);
      if (event) applyMailEvent(queryClient, event);
    });
    hub.onreconnecting(() => wanted && setStatus('reconnecting'));
    hub.onreconnected(() => {
      if (!wanted) return;
      setStatus('connected');
      resync(); // what happened while the channel was down is not known
    });
    hub.onclose(() => wanted && setStatus('disconnected'));

    const start = async (attempt: number): Promise<void> => {
      if (!wanted) return;
      try {
        await hub.start();
        if (!wanted) return;
        setStatus('connected');
        if (attempt > 0) resync();
      } catch (error) {
        if (!wanted) return;
        if (isRefusal(error)) {
          setStatus('disconnected');
          return;
        }
        setStatus('reconnecting');
        timer = setTimeout(() => void start(attempt + 1), retryDelay(attempt));
      }
    };
    void start(0);

    return () => {
      wanted = false;
      if (timer !== undefined) clearTimeout(timer);
      hub.off(EVENT_METHOD);
      if (hub.state !== HubConnectionState.Disconnected) void hub.stop();
    };
  }, [hub, queryClient]);

  return <RealtimeContext.Provider value={status}>{children}</RealtimeContext.Provider>;
}
