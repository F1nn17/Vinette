import { Outlet } from 'react-router';
import { RealtimeProvider } from '@/features/realtime';

/** The signed-in screens share one real-time channel: it opens when they are shown and closes when the user leaves them. */
export function RealtimeLayout() {
  return (
    <RealtimeProvider>
      <Outlet />
    </RealtimeProvider>
  );
}
