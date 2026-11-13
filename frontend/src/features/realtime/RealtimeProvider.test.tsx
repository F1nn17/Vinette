import { QueryClient, QueryClientProvider, type InfiniteData } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import { HubConnectionState } from '@microsoft/signalr';
import { RealtimeProvider } from './RealtimeProvider';
import { useRealtimeStatus } from './realtime-context';
import type { HubLike } from './connection';

class FakeHub implements HubLike {
  state: HubConnectionState = HubConnectionState.Disconnected;
  handlers = new Map<string, (...args: unknown[]) => void>();
  reconnecting: (() => void)[] = [];
  reconnected: (() => void)[] = [];
  closed: (() => void)[] = [];
  starts = 0;
  stops = 0;
  /** What the next starts do: `true` succeeds, an Error fails. */
  outcomes: (true | Error)[] = [true];

  start = vi.fn(() => {
    this.starts++;
    const outcome = this.outcomes.shift() ?? true;
    if (outcome === true) {
      this.state = HubConnectionState.Connected;
      return Promise.resolve();
    }
    return Promise.reject(outcome);
  });

  stop = vi.fn(() => {
    this.stops++;
    this.state = HubConnectionState.Disconnected;
    return Promise.resolve();
  });

  on = vi.fn((name: string, handler: (...args: unknown[]) => void) => {
    this.handlers.set(name, handler);
  });

  off = vi.fn((name: string) => {
    this.handlers.delete(name);
  });

  onreconnecting = vi.fn((handler: () => void) => {
    this.reconnecting.push(handler);
  });

  onreconnected = vi.fn((handler: () => void) => {
    this.reconnected.push(handler);
  });

  onclose = vi.fn((handler: () => void) => {
    this.closed.push(handler);
  });

  emit(value: unknown) {
    this.handlers.get('mailEvent')?.(value);
  }
}

function Status() {
  return <p data-testid="status">{useRealtimeStatus()}</p>;
}

function setup(hub: FakeHub, client = new QueryClient()) {
  const view = render(
    <QueryClientProvider client={client}>
      <RealtimeProvider connect={() => hub}>
        <Status />
      </RealtimeProvider>
    </QueryClientProvider>,
  );
  return { client, ...view };
}

const status = () => screen.getByTestId('status').textContent;

const listKey = ['messages', 'INBOX', 'all', true] as const;
const list = (): InfiniteData<{ items: { id: string }[]; next: null; total: number }> => ({
  pages: [{ items: [{ id: 'SU5CT1g.1.1' }], next: null, total: 1 }],
  pageParams: [null],
});

afterEach(() => {
  vi.useRealTimers();
});

describe('RealtimeProvider', () => {
  it('connects, and says so', async () => {
    const hub = new FakeHub();

    setup(hub);

    expect(status()).toBe('connecting');
    await act(async () => undefined);
    expect(hub.starts).toBe(1);
    expect(status()).toBe('connected');
  });

  it('puts the events it is sent into the caches', async () => {
    const hub = new FakeHub();
    const { client } = setup(hub);
    client.setQueryData(listKey, list());
    await act(async () => undefined);

    act(() =>
      hub.emit({
        type: 'arrived',
        folder: 'INBOX',
        message: { id: 'SU5CT1g.1.2', seen: false, flagged: false },
      }),
    );

    const items = client.getQueryData<ReturnType<typeof list>>(listKey)?.pages[0]?.items;
    expect(items?.map((i) => i.id)).toEqual(['SU5CT1g.1.2', 'SU5CT1g.1.1']);
  });

  it('ignores what it cannot read', async () => {
    const hub = new FakeHub();
    const { client } = setup(hub);
    client.setQueryData(listKey, list());
    await act(async () => undefined);

    act(() => {
      hub.emit({ type: 'newfangled', folder: 'INBOX' });
      hub.emit('text');
      hub.emit(null);
    });

    expect(client.getQueryData<ReturnType<typeof list>>(listKey)?.pages[0]?.items).toHaveLength(1);
    expect(status()).toBe('connected');
  });

  it('shows a lost connection while it is being restored, and reads everything again when it is back', async () => {
    const hub = new FakeHub();
    const { client } = setup(hub);
    client.setQueryData(listKey, list());
    await act(async () => undefined);

    act(() => hub.reconnecting.forEach((h) => h()));
    expect(status()).toBe('reconnecting');
    expect(client.getQueryState(listKey)?.isInvalidated).toBeFalsy();
    act(() => hub.reconnected.forEach((h) => h()));

    expect(status()).toBe('connected');
    expect(client.getQueryState(listKey)?.isInvalidated).toBe(true);
  });

  it('says "disconnected" when the library gives up', async () => {
    const hub = new FakeHub();
    setup(hub);
    await act(async () => undefined);

    act(() => hub.closed.forEach((h) => h()));

    expect(status()).toBe('disconnected');
  });

  it('tries the first connection again, with pauses, until it succeeds, and then reads everything again', async () => {
    vi.useFakeTimers();
    const hub = new FakeHub();
    hub.outcomes = [new Error('Failed to fetch'), new Error('Failed to fetch'), true];
    const client = new QueryClient();
    client.setQueryData(listKey, list());

    setup(hub, client);
    await act(async () => undefined);
    expect(hub.starts).toBe(1);
    expect(status()).toBe('reconnecting');

    await act(async () => vi.advanceTimersByTimeAsync(1300));
    expect(hub.starts).toBe(2);
    await act(async () => vi.advanceTimersByTimeAsync(2600));
    expect(hub.starts).toBe(3);

    expect(status()).toBe('connected');
    expect(client.getQueryState(listKey)?.isInvalidated).toBe(true);
  });

  it('does not keep trying when the session is refused', async () => {
    vi.useFakeTimers();
    const hub = new FakeHub();
    hub.outcomes = [new Error("Unauthorized: Status code '401'")];

    setup(hub);
    await act(async () => undefined);
    await act(async () => vi.advanceTimersByTimeAsync(120_000));

    expect(hub.starts).toBe(1);
    expect(status()).toBe('disconnected');
  });

  it('closes the connection and stops listening when it is left, and does not try again', async () => {
    vi.useFakeTimers();
    const hub = new FakeHub();
    hub.outcomes = [new Error('Failed to fetch')];
    const { unmount } = setup(hub);
    await act(async () => undefined);

    unmount();
    await act(async () => vi.advanceTimersByTimeAsync(120_000));

    expect(hub.off).toHaveBeenCalledWith('mailEvent');
    expect(hub.starts).toBe(1);
  });

  it('stops a connection that is up when it is left', async () => {
    const hub = new FakeHub();
    const { unmount } = setup(hub);
    await act(async () => undefined);

    unmount();

    expect(hub.stops).toBe(1);
  });

  it('lets the screens work when no connection can be made at all', () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <RealtimeProvider
          connect={() => {
            throw new Error('Cannot resolve /hubs/mail');
          }}
        >
          <Status />
        </RealtimeProvider>
      </QueryClientProvider>,
    );

    expect(status()).toBe('disconnected');
  });
});
