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

const FOLDERS = [folder('INBOX', 'inbox'), folder('Archive', 'archive'), folder('Trash', 'trash')];

// `SU5CT1g` is "INBOX" and `QXJjaGl2ZQ` is "Archive" in the way identifiers carry the folder.
const IN_INBOX = 'SU5CT1g.1.7';
const IN_ARCHIVE = 'QXJjaGl2ZQ.1.3';

const mail = (id: string, subject: string, labels: string[]) => ({
  id,
  from: { name: 'Anna Smirnova', address: 'anna@studio.example' },
  to: [{ name: '', address: 'user@example.com' }],
  subject,
  date: new Date().toISOString(),
  seen: true,
  flagged: false,
  answered: false,
  draft: false,
  hasAttachments: false,
  labels,
  size: 100,
  preview: 'Hello',
});

const detailOf = (id: string, subject: string, labels: string[]) => ({
  ...mail(id, subject, labels),
  cc: [],
  bodyFormat: 'text',
  body: 'Hello',
  blockedExternalResources: 0,
  canShowImages: false,
  imagesShown: 0,
  hasExternalImages: false,
  imagesTrusted: false,
  truncated: false,
  canShowFull: false,
  sender: { verdict: 'unknown', domain: null },
  attachments: [],
});

const rowsOf = (server: Pick<Server, 'labels'>) => [
  mail(IN_INBOX, 'Budget for November', server.labels[IN_INBOX] ?? []),
  mail(IN_ARCHIVE, 'Old contract', server.labels[IN_ARCHIVE] ?? []),
];

interface Server {
  /** The labels each message has now: the server keeps what was put on and taken off. */
  labels: Record<string, string[]>;
  urls: string[];
  patches: { id: string; body: { add: string[] | null; remove: string[] | null } }[];
  batches: { ids: string[]; action: string }[];
}

function mockServer(): Server {
  const server: Server = {
    labels: { [IN_INBOX]: ['Work'], [IN_ARCHIVE]: ['Work'] },
    urls: [],
    patches: [],
    batches: [],
  };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const path = decodeURI(url.pathname);
      server.urls.push(path + url.search);
      if (path === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (path === '/api/v1/folders') return Promise.resolve(json({ folders: FOLDERS }));
      if (path === '/api/v1/settings')
        return Promise.resolve(
          json({
            density: 'comfortable',
            language: null,
            labelColors: {},
            trustedImageSenders: [],
          }),
        );
      if (path === '/api/v1/labels')
        return Promise.resolve(
          json({
            labels: [
              { name: 'Work', color: null },
              { name: 'Home', color: null },
            ],
          }),
        );
      if (path === '/api/v1/labels/Work/messages')
        return Promise.resolve(json({ items: rowsOf(server), next: null, total: 2 }));
      if (path === '/api/v1/folders/INBOX/messages')
        return Promise.resolve(json({ items: rowsOf(server).slice(0, 1), next: null, total: 1 }));
      if (path === '/api/v1/messages/batch') {
        server.batches.push(JSON.parse(String(init?.body)) as Server['batches'][number]);
        return Promise.resolve(json({ changed: 1, folder: 'Archive', moved: [] }));
      }
      const labels = /^\/api\/v1\/messages\/([^/]+)\/labels$/u.exec(path);
      if (labels && init?.method === 'PATCH') {
        const id = labels[1] ?? '';
        const body = JSON.parse(String(init.body)) as Server['patches'][number]['body'];
        server.patches.push({ id, body });
        server.labels[id] = [
          ...(server.labels[id] ?? []).filter((l) => !body.remove?.includes(l)),
          ...(body.add ?? []),
        ];
        return Promise.resolve(json({ labels: server.labels[id] }));
      }
      if (path === `/api/v1/messages/${IN_INBOX}`)
        return Promise.resolve(
          json(detailOf(IN_INBOX, 'Budget for November', server.labels[IN_INBOX] ?? [])),
        );
      if (path === `/api/v1/messages/${IN_ARCHIVE}`)
        return Promise.resolve(
          json(detailOf(IN_ARCHIVE, 'Old contract', server.labels[IN_ARCHIVE] ?? [])),
        );
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
    [
      { path: '/mail/:folder/:messageId?', element: <MailboxPage /> },
      { path: '/mail/@label/:name/:messageId?', element: <MailboxPage /> },
    ],
    { initialEntries: [url] },
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

describe('the list of a label', () => {
  it('shows the messages of the label from all folders, under its name, without filters', async () => {
    const server = mockServer();
    renderPage('/mail/@label/Work');

    expect(await screen.findByRole('heading', { level: 1, name: 'Work' })).toBeInTheDocument();
    const first = await screen.findByRole('link', { name: /Budget for November/ });
    const second = screen.getByRole('link', { name: /Old contract/ });
    expect(first).toHaveAttribute('href', `/mail/@label/Work/${IN_INBOX}`);
    expect(second).toHaveAttribute('href', `/mail/@label/Work/${IN_ARCHIVE}`);
    expect(screen.queryByRole('group', { name: 'Фильтры' })).not.toBeInTheDocument();
    expect(server.urls).toContain('/api/v1/labels/Work/messages');
  });

  it('has no boxes for selecting: an action is for one folder, and these are in several', async () => {
    mockServer();
    renderPage('/mail/@label/Work');
    await screen.findByRole('link', { name: /Budget for November/ });

    expect(screen.queryByRole('checkbox', { name: /Выбрать письмо/ })).not.toBeInTheDocument();
  });

  it('opens a message in place and offers the actions that fit the folder it is in', async () => {
    mockServer();
    const user = userEvent.setup();
    const router = renderPage('/mail/@label/Work');

    await user.click(await screen.findByRole('link', { name: /Old contract/ }));

    await screen.findByRole('heading', { name: 'Old contract' });
    expect(router.state.location.pathname).toBe(`/mail/@label/Work/${IN_ARCHIVE}`);
    // it is in the archive: no "Archive" button, but "Spam" is there
    await waitFor(() =>
      expect(within(reader()).queryByRole('button', { name: 'В архив' })).not.toBeInTheDocument(),
    );
    expect(within(reader()).getByRole('button', { name: 'В спам' })).toBeInTheDocument();
  });

  it('returns to the list of the label (not to a folder) after an action on the open message', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    const router = renderPage(`/mail/@label/Work/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'Удалить' })).toBeEnabled(),
    );

    await user.click(within(reader()).getByRole('button', { name: 'Удалить' }));

    await waitFor(() =>
      expect(server.batches).toEqual([{ ids: [IN_INBOX], action: 'trash', to: null }]),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/@label/Work'));
  });

  it('marks no folder of the sidebar as the current one and the label as the current one', async () => {
    mockServer();
    renderPage('/mail/@label/Work');

    const label = await screen.findByRole('link', { name: 'Work', current: 'page' });
    expect(label).toHaveAttribute('href', '/mail/@label/Work');
    const folders = screen.getByRole('navigation', { name: 'Папки' });
    expect(within(folders).getByRole('link', { name: /Входящие/u })).not.toHaveAttribute(
      'aria-current',
    );
  });
});

describe('labels on the open message', () => {
  it('opens the panel with the labels of the message and takes one off', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });

    await user.click(within(reader()).getByRole('button', { name: 'Метки' }));
    const work = await screen.findByRole('checkbox', { name: 'Work' });
    expect(work).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Home' })).not.toBeChecked();
    await user.click(work);

    await waitFor(() =>
      expect(server.patches).toEqual([{ id: IN_INBOX, body: { add: null, remove: ['Work'] } }]),
    );
    // the label is gone from the message at once
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Work' })).not.toBeChecked());
  });

  it('puts a new label on the message', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });

    await user.click(within(reader()).getByRole('button', { name: 'Метки' }));
    await user.type(
      await screen.findByRole('textbox', { name: 'Название новой метки' }),
      'Отпуск{Enter}',
    );

    await waitFor(() =>
      expect(server.patches).toEqual([{ id: IN_INBOX, body: { add: ['Отпуск'], remove: null } }]),
    );
  });

  it('closes the panel on Escape', async () => {
    mockServer();
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });

    await user.click(within(reader()).getByRole('button', { name: 'Метки' }));
    await screen.findByRole('checkbox', { name: 'Work' });
    await user.keyboard('{Escape}');

    expect(screen.queryByRole('checkbox', { name: 'Work' })).not.toBeInTheDocument();
  });

  it('is in "More" too, for a phone, where the bar has no buttons of its own', async () => {
    mockServer();
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });

    await user.click(within(reader()).getByRole('button', { name: 'Ещё' }));
    await user.click(
      within(screen.getByRole('group', { name: 'Ещё' })).getByRole('button', { name: 'Метки' }),
    );

    expect(await screen.findByRole('checkbox', { name: 'Work' })).toBeInTheDocument();
  });
});

describe('labels on the selected messages', () => {
  it('has a "Labels" button in the bar of the selection, which puts the label on all of them', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage('/mail/inbox');
    await user.click(
      await screen.findByRole('checkbox', { name: 'Выбрать письмо «Budget for November»' }),
    );

    const bar = await screen.findByRole('toolbar', { name: 'Действия с выбранными письмами' });
    await user.click(within(bar).getByRole('button', { name: 'Метки' }));
    await user.click(await screen.findByRole('checkbox', { name: 'Home' }));

    await waitFor(() =>
      expect(server.patches).toEqual([{ id: IN_INBOX, body: { add: ['Home'], remove: null } }]),
    );
  });
});

describe('the key l', () => {
  it('opens the labels of the open message', async () => {
    mockServer();
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });

    await user.keyboard('l');

    expect(await screen.findByRole('checkbox', { name: 'Work' })).toBeChecked();
  });

  it('opens the labels of the selected messages rather than of the open one', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage(`/mail/inbox/${IN_INBOX}`);
    await screen.findByRole('heading', { name: 'Budget for November' });
    await user.click(
      await screen.findByRole('checkbox', { name: 'Выбрать письмо «Budget for November»' }),
    );

    await user.keyboard('l');
    await user.click(await screen.findByRole('checkbox', { name: 'Home' }));

    await waitFor(() =>
      expect(server.patches).toEqual([{ id: IN_INBOX, body: { add: ['Home'], remove: null } }]),
    );
    expect(screen.getAllByRole('checkbox', { name: 'Home' })).toHaveLength(1); // one panel only
  });

  it('does nothing when no message is open or selected, and while typing in the new-label field', async () => {
    mockServer();
    const user = userEvent.setup();
    renderPage('/mail/inbox');
    await screen.findByRole('link', { name: /Budget for November/ });

    await user.keyboard('l');

    expect(screen.queryByRole('checkbox', { name: 'Work' })).not.toBeInTheDocument();
  });

  it('is listed in the window of the keys', async () => {
    mockServer();
    const user = userEvent.setup();
    renderPage('/mail/inbox');
    await screen.findByRole('link', { name: /Budget for November/ });

    await user.keyboard('?');

    const dialog = await screen.findByRole('dialog', { name: 'Горячие клавиши' });
    expect(within(dialog).getByText('Метки')).toBeInTheDocument();
  });
});
