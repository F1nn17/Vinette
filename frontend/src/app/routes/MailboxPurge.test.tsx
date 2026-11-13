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

const FOLDERS = [folder('INBOX', 'inbox'), folder('Junk', 'junk'), folder('Trash', 'trash')];

// `VHJhc2g` is "Trash" in the way identifiers carry the folder.
const ID = 'VHJhc2g.1.4';

const mail = {
  id: ID,
  from: { name: 'Anna Smirnova', address: 'anna@studio.example' },
  to: [{ name: '', address: 'user@example.com' }],
  subject: 'Old invoice',
  date: new Date().toISOString(),
  seen: true,
  flagged: false,
  answered: false,
  draft: false,
  hasAttachments: false,
  labels: [],
  size: 100,
  preview: 'Hello',
};

const detail = {
  ...mail,
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
};

interface Server {
  batches: { ids: string[]; action: string }[];
  empties: string[];
  failEmpty: boolean;
}

function mockServer(): Server {
  const server: Server = { batches: [], empties: [], failEmpty: false };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const path = decodeURI(url.pathname);
      if (path === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (path === '/api/v1/folders') return Promise.resolve(json({ folders: FOLDERS }));
      if (path === '/api/v1/labels') return Promise.resolve(json({ labels: [] }));
      if (path === '/api/v1/settings')
        return Promise.resolve(
          json({
            density: 'comfortable',
            language: null,
            labelColors: {},
            trustedImageSenders: [],
          }),
        );
      if (path === '/api/v1/messages/batch') {
        server.batches.push(JSON.parse(String(init?.body)) as Server['batches'][number]);
        return Promise.resolve(json({ changed: 1, folder: null, moved: [] }));
      }
      const empty = /^\/api\/v1\/folders\/(.+)\/empty$/u.exec(path);
      if (empty && init?.method === 'POST') {
        server.empties.push(empty[1] ?? '');
        return Promise.resolve(
          server.failEmpty
            ? json({ code: 'mail_unavailable', title: 'x' }, { status: 503 })
            : json({ deleted: 3 }),
        );
      }
      if (path === '/api/v1/folders/Trash/messages' || path === '/api/v1/folders/Junk/messages')
        return Promise.resolve(json({ items: [mail], next: null, total: 1 }));
      if (path.endsWith('/messages'))
        return Promise.resolve(json({ items: [], next: null, total: 0 }));
      if (path === `/api/v1/messages/${ID}`) return Promise.resolve(json(detail));
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

describe('emptying the trash and the spam folder', () => {
  it('offers it in the trash, asks first, and then empties the folder and says so', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage('/mail/trash');

    await user.click(await screen.findByRole('button', { name: 'Очистить корзину' }));
    const dialog = screen.getByRole('dialog', { name: 'Очистить корзину?' });
    expect(dialog).toHaveTextContent('Это нельзя отменить');
    expect(server.empties).toEqual([]);
    await user.click(within(dialog).getByRole('button', { name: 'Удалить навсегда' }));

    await waitFor(() => expect(server.empties).toEqual(['Trash']));
    expect(await screen.findByText('Корзина очищена')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('does nothing when the question is cancelled', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage('/mail/trash');

    await user.click(await screen.findByRole('button', { name: 'Очистить корзину' }));
    await user.click(screen.getByRole('button', { name: 'Отмена' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(server.empties).toEqual([]);
  });

  it('is "Empty spam" in the spam folder, with the same question', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage('/mail/junk');

    await user.click(await screen.findByRole('button', { name: 'Очистить спам' }));
    await user.click(
      within(screen.getByRole('dialog', { name: 'Очистить спам?' })).getByRole('button', {
        name: 'Удалить навсегда',
      }),
    );

    await waitFor(() => expect(server.empties).toEqual(['Junk']));
    expect(await screen.findByText('Спам очищен')).toBeInTheDocument();
  });

  it('is not offered in the inbox', async () => {
    mockServer();
    renderPage('/mail/inbox');

    await screen.findByRole('heading', { level: 1, name: 'Входящие' });
    expect(screen.queryByRole('button', { name: /Очистить/u })).not.toBeInTheDocument();
  });

  it('says in plain words when the folder could not be emptied', async () => {
    const server = mockServer();
    server.failEmpty = true;
    const user = userEvent.setup();
    renderPage('/mail/trash');

    await user.click(await screen.findByRole('button', { name: 'Очистить корзину' }));
    await user.click(screen.getByRole('button', { name: 'Удалить навсегда' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
  });
});

describe('deleting for good from the trash', () => {
  it('offers "Delete forever" instead of "Delete" for the open message, asks, and sends the batch', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    const router = renderPage(`/mail/trash/${ID}`);
    await screen.findByRole('heading', { name: 'Old invoice' });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'Удалить навсегда' })).toBeEnabled(),
    );
    expect(within(reader()).queryByRole('button', { name: 'Удалить' })).not.toBeInTheDocument();

    await user.click(within(reader()).getByRole('button', { name: 'Удалить навсегда' }));
    const dialog = screen.getByRole('dialog', { name: 'Удалить письмо навсегда?' });
    expect(server.batches).toEqual([]);
    await user.click(within(dialog).getByRole('button', { name: 'Удалить навсегда' }));

    await waitFor(() =>
      expect(server.batches).toEqual([{ ids: [ID], action: 'delete', to: null }]),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/trash'));
    expect(await screen.findByText('Письмо удалено навсегда')).toBeInTheDocument();
  });

  it('keeps the message and stays on it when the question is cancelled', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    const router = renderPage(`/mail/trash/${ID}`);
    await screen.findByRole('heading', { name: 'Old invoice' });
    await waitFor(() =>
      expect(within(reader()).getByRole('button', { name: 'Удалить навсегда' })).toBeEnabled(),
    );

    await user.click(within(reader()).getByRole('button', { name: 'Удалить навсегда' }));
    await user.click(screen.getByRole('button', { name: 'Отмена' }));

    expect(server.batches).toEqual([]);
    expect(router.state.location.pathname).toBe(`/mail/trash/${ID}`);
  });

  it('has the button in the bar of a selection too', async () => {
    const server = mockServer();
    const user = userEvent.setup();
    renderPage('/mail/trash');
    await user.click(await screen.findByRole('checkbox', { name: 'Выбрать письмо «Old invoice»' }));

    const bar = await screen.findByRole('toolbar', { name: 'Действия с выбранными письмами' });
    expect(within(bar).queryByRole('button', { name: 'Удалить' })).not.toBeInTheDocument();
    await user.click(within(bar).getByRole('button', { name: 'Удалить навсегда' }));
    await user.click(
      within(screen.getByRole('dialog', { name: 'Удалить письмо навсегда?' })).getByRole('button', {
        name: 'Удалить навсегда',
      }),
    );

    await waitFor(() =>
      expect(server.batches).toEqual([{ ids: [ID], action: 'delete', to: null }]),
    );
  });

  it('is not offered outside the trash', async () => {
    mockServer();
    renderPage('/mail/inbox');
    await screen.findByRole('heading', { level: 1, name: 'Входящие' });

    expect(screen.queryByRole('button', { name: 'Удалить навсегда' })).not.toBeInTheDocument();
  });
});
