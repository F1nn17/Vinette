import { Outlet } from 'react-router';
import { RealtimeProvider } from '@/features/realtime';
import { ScheduledWatcher } from '@/features/scheduled';
import { LanguageSync } from '@/features/settings';

/**
 * The signed-in screens share one real-time channel: it opens when they are shown and closes when the user leaves them. They also share the
 * watch over the messages put off, which says once when one did not go, and the language from the settings.
 */
export function RealtimeLayout() {
  return (
    <RealtimeProvider>
      <ScheduledWatcher />
      <LanguageSync />
      <Outlet />
    </RealtimeProvider>
  );
}
