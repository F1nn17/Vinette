import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { RealtimeContext, type RealtimeStatus } from '@/features/realtime';
import { CONNECTION_GRACE_MS, useConnectionLost } from './use-online';

function setup(initial: RealtimeStatus) {
  let status = initial;
  const wrapper = ({ children }: { children: ReactNode }) => (
    <RealtimeContext.Provider value={status}>{children}</RealtimeContext.Provider>
  );
  const view = renderHook(() => useConnectionLost(), { wrapper });
  return {
    ...view,
    set: (next: RealtimeStatus) => {
      status = next;
      view.rerender();
    },
  };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('useConnectionLost', () => {
  it.each(['connecting', 'connected', 'disconnected'] as const)(
    'is false while the channel is "%s"',
    (status) => {
      const { result } = setup(status);

      act(() => vi.advanceTimersByTime(CONNECTION_GRACE_MS * 3));

      expect(result.current).toBe(false);
    },
  );

  it('is true only once the channel has been trying to come back for longer than the grace', () => {
    const { result } = setup('reconnecting');
    expect(result.current).toBe(false);

    act(() => vi.advanceTimersByTime(CONNECTION_GRACE_MS - 1));
    expect(result.current).toBe(false);
    act(() => vi.advanceTimersByTime(2));

    expect(result.current).toBe(true);
  });

  it('does not show a blink: a channel that is back within the grace never makes it true', () => {
    const { result, set } = setup('connected');

    set('reconnecting');
    act(() => vi.advanceTimersByTime(CONNECTION_GRACE_MS - 500));
    set('connected');
    act(() => vi.advanceTimersByTime(CONNECTION_GRACE_MS * 2));

    expect(result.current).toBe(false);
  });

  it('goes away at once when the channel is back, and the next loss gets its own grace', () => {
    const { result, set } = setup('reconnecting');
    act(() => vi.advanceTimersByTime(CONNECTION_GRACE_MS + 1));
    expect(result.current).toBe(true);

    set('connected');
    expect(result.current).toBe(false);
    set('reconnecting');
    expect(result.current).toBe(false); // not at once: the grace starts again
    act(() => vi.advanceTimersByTime(CONNECTION_GRACE_MS + 1));

    expect(result.current).toBe(true);
  });
});
