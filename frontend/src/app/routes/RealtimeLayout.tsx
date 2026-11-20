import { Outlet } from 'react-router';
import { RealtimeProvider } from '@/features/realtime';
import { ScheduledWatcher } from '@/features/scheduled';

/**
 * The signed-in screens share one real-time channel: it opens when they are shown and closes when the user leaves them. They also share the
 * watch over the messages put off, which says once when one did not go.
 */
export function RealtimeLayout() {
  return (
    <RealtimeProvider>
      <ScheduledWatcher />
      <Outlet />
    </RealtimeProvider>
  );
}
