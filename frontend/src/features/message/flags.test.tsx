import { QueryClient, QueryClientProvider, type InfiniteData } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ReadingPane } from './ReadingPane';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const message = (id: string, extra: Record<string, unknown> = {}) => ({
  id,
  subject: `Subject ${id}`,
  from: { name: 'Anna', address: 'anna@example.org' },
  to: [{ name: '', address: 'user@example.com' }],
  cc: [],
  date: new Date(2026, 9, 2, 14, 32).toISOString(),
  seen: false,
  flagged: false,
  answered: false,
  draft: false,
  labels: [],
  bodyFormat: 'text',
  body: '<div>hello</div>',
  blockedExternalResources: 0,
  canShowImages: false,
  imagesShown: 0,
  truncated: false,
  sender: { verdict: 'unknown', domain: null },
  attachments: [],
  ...extra,
});

interface Server {
  /** What the server holds per message id. */
  messages: Record<string, ReturnType<typeof message>>;
  patches: { id: string; body: Record<string, boolean | null> }[];
  /** Makes every change fail with this status. */
  failWith?: number;
  /** Holds the answer of a change until it is released. */
  hold?: Promise<void>;
  gets: string[];
}

function mockServer(messages: ReturnType<typeof message>[]): Server {
  const server: Server = {
    messages: Object.fromEntries(messages.map((m) => [m.id, m])),
    patches: [],
    gets: [],
  };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      if (url.pathname === '/api/v1/csrf') return json({ token: 'csrf' });
      const flagsMatch = /^\/api\/v1\/messages\/([^/]+)\/flags$/u.exec(url.pathname);
      if (flagsMatch && init?.method === 'PATCH') {
        const id = decodeURIComponent(flagsMatch[1]!);
        const body = JSON.parse(String(init.body)) as Record<string, boolean | null>;
        server.patches.push({ id, body });
        if (server.hold) await server.hold;
        if (server.failWith)
          return json({ code: 'mail_unavailable', title: 'x' }, { status: server.failWith });
        const current = server.messages[id]!;
        server.messages[id] = {
          ...current,
          seen: body.seen ?? current.seen,
          flagged: body.flagged ?? current.flagged,
        };
        const { seen, flagged, answered, draft } = server.messages[id];
        return json({ seen, flagged, answered, draft });
      }
      const messageMatch = /^\/api\/v1\/messages\/([^/]+)$/u.exec(url.pathname);
      if (messageMatch) {
        const id = decodeURIComponent(messageMatch[1]!);
        server.gets.push(id);
        return json(server.messages[id]);
      }
      return json({ code: 'not_found' }, { status: 404 });
    }),
  );
  return server;
}

function renderPane(queryClient: QueryClient, id: string) {
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ReadingPane messageId={id} address="user@example.com" />
    </QueryClientProvider>,
  );
  return {
    ...view,
    open: (next: string) =>
      view.rerender(
        <QueryClientProvider client={queryClient}>
          <ReadingPane messageId={next} address="user@example.com" />
        </QueryClientProvider>,
      ),
  };
}

const newClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

/** A cached page of a list holding these ids, as the list pane would have it. */
function seedList(client: QueryClient, ids: string[]) {
  const data: InfiniteData<{
    items: { id: string; seen: boolean; flagged: boolean }[];
    next: string | null;
    total: number;
  }> = {
    pageParams: [null],
    pages: [
      {
        items: ids.map((id) => ({ id, seen: false, flagged: false })),
        next: null,
        total: ids.length,
      },
    ],
  };
  client.setQueryData(['messages', 'INBOX', 'all', true], data);
  return () =>
    (client.getQueryData(['messages', 'INBOX', 'all', true]) as typeof data).pages[0]!.items;
}

afterEach(() => vi.unstubAllGlobals());

describe('opening a message reads it', () => {
  it('marks an unread message as read, once', async () => {
    const server = mockServer([message('a.1.1')]);
    renderPane(newClient(), 'a.1.1');

    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    await waitFor(() => expect(server.patches).toHaveLength(1));

    expect(server.patches[0]).toEqual({ id: 'a.1.1', body: { seen: true, flagged: null } });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(server.patches).toHaveLength(1);
  });

  it('leaves a message that is already read alone', async () => {
    const server = mockServer([message('a.1.1', { seen: true })]);
    renderPane(newClient(), 'a.1.1');

    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(server.patches).toEqual([]);
  });

  it('reads each message that is opened, and one message once however often it comes back', async () => {
    const server = mockServer([message('a.1.1'), message('a.1.2')]);
    const client = newClient();
    const view = renderPane(client, 'a.1.1');
    await waitFor(() => expect(server.patches).toHaveLength(1));

    view.open('a.1.2');
    await waitFor(() => expect(server.patches).toHaveLength(2));
    view.open('a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(server.patches.map((p) => p.id)).toEqual(['a.1.1', 'a.1.2']);
  });

  it('shows the message as read and drops its dot in the list at once, before the server has answered', async () => {
    let release: () => void = () => undefined;
    const server = mockServer([message('a.1.1')]);
    server.hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const client = newClient();
    const rows = seedList(client, ['a.1.1', 'a.1.2']);
    renderPane(client, 'a.1.1');

    await waitFor(() => expect(server.patches).toHaveLength(1));

    expect(rows()[0]!.seen).toBe(true); // the server has not answered yet
    expect(rows()[1]!.seen).toBe(false); // another row is untouched
    expect((client.getQueryData(['message', 'a.1.1']) as { seen: boolean }).seen).toBe(true);
    release();
  });

  it('takes the folder counters again afterwards, not the lists', async () => {
    mockServer([message('a.1.1')]);
    const client = newClient();
    const invalidated: unknown[] = [];
    const original = client.invalidateQueries.bind(client);
    client.invalidateQueries = ((filters: { queryKey?: unknown[] }) => {
      invalidated.push(filters?.queryKey);
      return original(filters as never);
    }) as typeof client.invalidateQueries;
    renderPane(client, 'a.1.1');

    await waitFor(() => expect(invalidated).toContainEqual(['folders']));

    expect(invalidated).not.toContainEqual(['messages']);
  });

  describe('when the server refuses', () => {
    it('puts the message and its row back as unread, says nothing and does not insist', async () => {
      const server = mockServer([message('a.1.1')]);
      server.failWith = 503;
      const client = newClient();
      const rows = seedList(client, ['a.1.1']);
      renderPane(client, 'a.1.1');

      await waitFor(() => expect(server.patches).toHaveLength(1));
      await waitFor(() => expect(rows()[0]!.seen).toBe(false));
      await new Promise((resolve) => setTimeout(resolve, 80));

      expect((client.getQueryData(['message', 'a.1.1']) as { seen: boolean }).seen).toBe(false);
      expect(server.patches).toHaveLength(1);
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
  });
});

describe('the flag', () => {
  const flagButton = () => screen.getByRole('button', { name: 'Флажок' });

  it('can be used, and shows the state of the message', async () => {
    mockServer([message('a.1.1', { seen: true, flagged: true })]);
    renderPane(newClient(), 'a.1.1');

    await screen.findByRole('heading', { name: 'Subject a.1.1' });

    expect(flagButton()).toBeEnabled();
    expect(flagButton()).toHaveAttribute('aria-pressed', 'true');
  });

  it('is set by a click: pressed at once, saved, and the list row follows', async () => {
    let release: () => void = () => undefined;
    const server = mockServer([message('a.1.1', { seen: true })]);
    const client = newClient();
    const rows = seedList(client, ['a.1.1']);
    const user = userEvent.setup();
    renderPane(client, 'a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    server.hold = new Promise<void>((resolve) => {
      release = resolve;
    });

    await user.click(flagButton());

    await waitFor(() => expect(flagButton()).toHaveAttribute('aria-pressed', 'true')); // before the answer
    expect(rows()[0]!.flagged).toBe(true);
    release();
    await waitFor(() =>
      expect(server.patches).toEqual([{ id: 'a.1.1', body: { seen: null, flagged: true } }]),
    );
    expect(server.messages['a.1.1']!.flagged).toBe(true);
  });

  it('is removed by a second click', async () => {
    const server = mockServer([message('a.1.1', { seen: true, flagged: true })]);
    const user = userEvent.setup();
    renderPane(newClient(), 'a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });

    await user.click(flagButton());

    await waitFor(() => expect(flagButton()).toHaveAttribute('aria-pressed', 'false'));
    await waitFor(() => expect(server.messages['a.1.1']!.flagged).toBe(false));
  });

  it('survives a reload: a fresh session of the screen shows what the server holds', async () => {
    const server = mockServer([message('a.1.1', { seen: true })]);
    const user = userEvent.setup();
    const first = renderPane(newClient(), 'a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    await user.click(flagButton());
    await waitFor(() => expect(server.messages['a.1.1']!.flagged).toBe(true));
    first.unmount();

    renderPane(newClient(), 'a.1.1'); // nothing cached: as after a reload

    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    expect(flagButton()).toHaveAttribute('aria-pressed', 'true');
  });

  it('goes back when the server refuses, and says what happened in plain words', async () => {
    const server = mockServer([message('a.1.1', { seen: true })]);
    const client = newClient();
    const rows = seedList(client, ['a.1.1']);
    const user = userEvent.setup();
    renderPane(client, 'a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    server.failWith = 503;

    await user.click(flagButton());

    expect(await screen.findByRole('alert')).toHaveTextContent('Не удалось сохранить отметку');
    expect(flagButton()).toHaveAttribute('aria-pressed', 'false');
    expect(rows()[0]!.flagged).toBe(false);
  });

  it('takes the warning away after the next change that works', async () => {
    const server = mockServer([message('a.1.1', { seen: true })]);
    const user = userEvent.setup();
    renderPane(newClient(), 'a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });
    server.failWith = 503;
    await user.click(flagButton());
    await screen.findByRole('alert');
    server.failWith = undefined;

    await user.click(flagButton());

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(flagButton()).toHaveAttribute('aria-pressed', 'true');
  });

  it('is sent in order when clicked quickly twice', async () => {
    const server = mockServer([message('a.1.1', { seen: true })]);
    const user = userEvent.setup();
    renderPane(newClient(), 'a.1.1');
    await screen.findByRole('heading', { name: 'Subject a.1.1' });

    await user.click(flagButton());
    await user.click(flagButton());

    await waitFor(() => expect(server.patches).toHaveLength(2));
    expect(server.patches.map((p) => p.body.flagged)).toEqual([true, false]);
    await waitFor(() => expect(server.messages['a.1.1']!.flagged).toBe(false));
  });
});
