import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { consumeRemoval } from '@/features/realtime/echo';
import { ToastProvider } from '@/shared/toast';
import { useMessageBatch } from './batch';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

// "SU5CT1g" is "INBOX" in the way identifiers carry the folder.
const ID = 'SU5CT1g.1.7';

function setup(batchStatus = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const path = new URL(String(input), 'http://localhost').pathname;
      if (path === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (path === '/api/v1/messages/batch')
        return Promise.resolve(
          batchStatus === 200
            ? json({ changed: 1, folder: 'Archive', moved: [] })
            : json({ code: 'mail_unavailable', title: 'x' }, { status: batchStatus }),
        );
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <ToastProvider>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </ToastProvider>
  );
  return { client, ...renderHook(() => useMessageBatch(), { wrapper }) };
}

afterEach(() => vi.unstubAllGlobals());

describe("the live channel's report of an action of our own (VIN-087)", () => {
  it.each(['archive', 'spam', 'trash', 'delete'] as const)(
    'is expected after "%s" and swallowed once',
    async (action) => {
      const { client, result } = setup();

      await act(async () => result.current.mutateAsync({ ids: [ID], action }));

      expect(consumeRemoval(client, 'INBOX', 1)).toBe(0); // the report of our own removal: nothing to do
      expect(consumeRemoval(client, 'INBOX', 1)).toBe(1); // a second one is somebody else's
    },
  );

  it('expects as many removals as there are messages', async () => {
    const { client, result } = setup();

    await act(async () =>
      result.current.mutateAsync({ ids: [ID, 'SU5CT1g.1.8', 'SU5CT1g.1.9'], action: 'trash' }),
    );

    expect(consumeRemoval(client, 'INBOX', 3)).toBe(0);
  });

  it.each(['read', 'unread', 'flag', 'unflag'] as const)(
    'is not expected after "%s", which removes nothing',
    async (action) => {
      const { client, result } = setup();

      await act(async () => result.current.mutateAsync({ ids: [ID], action }));

      expect(consumeRemoval(client, 'INBOX', 1)).toBe(1);
    },
  );

  it('is taken back when the server refuses, so that a real removal is not hidden', async () => {
    const { client, result } = setup(503);

    await act(async () => {
      await result.current.mutateAsync({ ids: [ID], action: 'trash' }).catch(() => undefined);
    });
    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(consumeRemoval(client, 'INBOX', 1)).toBe(1);
  });
});
