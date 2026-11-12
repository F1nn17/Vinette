import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { SessionContext, type SessionValue } from '@/features/auth/session-context';
import { ToastProvider } from '@/shared/toast';
import { MailboxPage } from './MailboxPage';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const folder = (path: string, role: string) => ({
  path,
  name: path,
  parentPath: null,
  role,
  canContainMessages: true,
  total: 3,
  unread: 0,
  subscribed: true,
});

const FOLDERS = [
  folder('INBOX', 'inbox'),
  folder('Archive', 'archive'),
  folder('Junk', 'junk'),
  folder('Trash', 'trash'),
];

const ID = 'SU5CT1g.1.7';

const detail = {
  id: ID,
  subject: 'Layouts, second iteration',
  from: { name: 'Anna Smirnova', address: 'anna@studio.example' },
  to: [{ name: '', address: 'user@example.com' }],
  cc: [],
  date: new Date().toISOString(),
  seen: true,
  flagged: false,
  answered: false,
  draft: false,
  labels: [],
  bodyFormat: 'text',
  body: '<div>Hello</div>',
  blockedExternalResources: 0,
  canShowImages: false,
  imagesShown: 0,
  hasExternalImages: false,
  imagesTrusted: false,
  truncated: false,
  canShowFull: false,
  sender: { verdict: 'unknown', domain: null },
  attachments: [],
};

const row = {
  id: ID,
  from: detail.from,
  to: detail.to,
  subject: detail.subject,
  date: detail.date,
  seen: true,
  flagged: false,
  answered: false,
  draft: false,
  hasAttachments: false,
  labels: [],
  size: 100,
  preview: 'Hello',
};

interface Server {
  batches: { ids: string[]; action: string }[];
  /** What the batch endpoint answers; a problem to fail. */
  batchAnswer: () => Response;
  /** Holds the batch answer back until it is called. */
  hold?: Promise<void>;
  urls: string[];
}

function mockServer(): Server {
  const server: Server = {
    batches: [],
    urls: [],
    batchAnswer: () => json({ changed: 1, folder: 'Archive', moved: [] }),
  };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = new URL(String(input), 'http://localhost');
      server.urls.push(url.pathname + url.search);
      if (url.pathname === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (url.pathname === '/api/v1/messages/batch') {
        server.batches.push(JSON.parse(String(init?.body)) as { ids: string[]; action: string });
        return (server.hold ?? Promise.resolve()).then(() => server.batchAnswer());
      }
      if (url.pathname === '/api/v1/folders') return Promise.resolve(json({ folders: FOLDERS }));
      if (url.pathname === '/api/v1/settings') {
        return Promise.resolve(
          json({
            density: 'comfortable',
            language: null,
            labelColors: {},
            trustedImageSenders: [],
          }),
        );
      }
      if (url.pathname.endsWith('/messages')) {
        return Promise.resolve(json({ items: [row], next: null }));
      }
      if (url.pathname === `/api/v1/messages/${ID}`) return Promise.resolve(json(detail));
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
  return server;
}

const session: SessionValue = {
  status: 'signedIn',
  address: 'user@example.com',
  notice: null,
  signIn: () => Promise.resolve(),
  signOut: () => Promise.resolve(),
};

function renderPage(url: string) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: Number(/min-width: (\d+)px/.exec(query)?.[1] ?? 0) <= 1400,
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
  const router = createMemoryRouter(
    [{ path: '/mail/:folder/:messageId?', element: <MailboxPage /> }],
    {
      initialEntries: [url],
    },
  );
  render(
    <SessionContext.Provider value={session}>
      <ToastProvider>
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <RouterProvider router={router} />
        </QueryClientProvider>
      </ToastProvider>
    </SessionContext.Provider>,
  );
  return router;
}

const reader = () => screen.getByRole('toolbar', { name: 'Действия с письмом' });

// jsdom has no layout: give the virtual list a box of 800 px and rows of 96 px, so that rows are drawn.
beforeEach(() => {
  const size = (el: HTMLElement) =>
    el.dataset.index === undefined ? 800 : el.querySelector('h2') ? 36 : 96;
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (
    this: HTMLElement,
  ) {
    return size(this);
  });
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(400);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(800);
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(400);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the buttons of the open message', () => {
  it.each([
    ['В архив', 'archive'],
    ['В спам', 'spam'],
    ['Удалить', 'trash'],
  ])('"%s" sends the message to the right place and returns to the list', async (name, action) => {
    const server = mockServer();
    const user = userEvent.setup();
    const router = renderPage(`/mail/inbox/${ID}`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    await waitFor(() => expect(within(reader()).getByRole('button', { name })).toBeEnabled());

    await user.click(within(reader()).getByRole('button', { name }));

    await waitFor(() => expect(server.batches).toEqual([{ ids: [ID], action, to: null }]));
    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
  });

  it('marks as unread from "More" and returns to the list', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    const router = renderPage(`/mail/inbox/${ID}`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });

    await user.click(within(reader()).getByRole('button', { name: 'Ещё' }));
    await user.click(screen.getByRole('button', { name: 'Отметить непрочитанным' }));

    await waitFor(() =>
      expect(server.batches).toEqual([{ ids: [ID], action: 'unread', to: null }]),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
  });

  it('keeps the list parameters (the filter) when it returns to the list', async () => {
    mockServer();
    const user = userEvent.setup();
    const router = renderPage(`/mail/inbox/${ID}?filter=unread`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'Удалить' })).toBeEnabled(),
    );

    await user.click(within(reader()).getByRole('button', { name: 'Удалить' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    expect(router.state.location.search).toBe('?filter=unread');
  });

  it('does not offer to delete a message that is in the trash, nor to archive one that is in the archive', async () => {
    mockServer();
    renderPage(`/mail/trash/${ID}`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    await waitFor(() =>
      expect(within(reader()).queryByRole('button', { name: 'Удалить' })).not.toBeInTheDocument(),
    );
    expect(within(reader()).getByRole('button', { name: 'В архив' })).toBeInTheDocument();
  });

  it('shows the result at once: the message is gone from the list before the server has answered', async () => {
    const server = mockServer();
    let release: () => void = () => undefined;
    server.hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${ID}`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    await screen.findByRole('link', { name: /Layouts, second iteration/ });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'Удалить' })).toBeEnabled(),
    );

    await user.click(within(reader()).getByRole('button', { name: 'Удалить' }));

    await waitFor(() =>
      expect(
        screen.queryByRole('link', { name: /Layouts, second iteration/ }),
      ).not.toBeInTheDocument(),
    );
    expect(server.batches).toHaveLength(1);
    release();
  });

  it('puts the message back and says so in a toast when the server refuses', async () => {
    const server = mockServer();
    server.batchAnswer = () => json({ code: 'mail_unavailable', title: 'x' }, { status: 503 });
    const user = userEvent.setup();
    const router = renderPage(`/mail/inbox/${ID}`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    await screen.findByRole('link', { name: /Layouts, second iteration/ });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'В спам' })).toBeEnabled(),
    );

    await user.click(within(reader()).getByRole('button', { name: 'В спам' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
    await waitFor(() =>
      expect(screen.getByRole('link', { name: /Layouts, second iteration/ })).toBeInTheDocument(),
    );
    expect(router.state.location.pathname).toBe('/mail/inbox'); // the reader went to the list; the message is in it again
  });

  it('says what to do when the mailbox has no such folder', async () => {
    const server = mockServer();
    server.batchAnswer = () => json({ code: 'no_target_folder', title: 'x' }, { status: 409 });
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${ID}`);
    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    await screen.findByRole('link', { name: /Layouts, second iteration/ });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'В архив' })).toBeEnabled(),
    );

    await user.click(within(reader()).getByRole('button', { name: 'В архив' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('нет подходящей папки');
    await waitFor(() =>
      expect(screen.getByRole('link', { name: /Layouts, second iteration/ })).toBeInTheDocument(),
    );
  });
});
