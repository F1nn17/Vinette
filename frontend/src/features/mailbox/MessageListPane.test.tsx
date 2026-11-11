import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { MessageListPane } from './MessageListPane';
import { labelColor } from './label-color';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const folder = (path: string, role: string, extra: Record<string, unknown> = {}) => ({
  path,
  name: path,
  parentPath: null,
  role,
  canContainMessages: true,
  total: 10,
  unread: 0,
  subscribed: true,
  ...extra,
});

const FOLDERS = [
  folder('INBOX', 'inbox', { unread: 4 }),
  folder('Sent', 'sent'),
  folder('Работа/Отчёты', 'other', { name: 'Отчёты' }),
];

const message = (n: number, extra: Record<string, unknown> = {}) => ({
  id: `m${n}`,
  from: { name: `Sender ${n}`, address: `s${n}@example.org` },
  to: [{ name: '', address: 'to@example.org' }],
  subject: `Subject ${n}`,
  date: new Date().toISOString(),
  seen: true,
  flagged: false,
  answered: false,
  draft: false,
  hasAttachments: false,
  labels: [],
  size: 100,
  preview: `Preview ${n}`,
  ...extra,
});

interface Server {
  urls: string[];
  pages: (url: URL) => unknown;
}

function mockServer(pages: (url: URL) => unknown): Server {
  const server: Server = { urls: [], pages };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = new URL(String(input), 'http://localhost');
      server.urls.push(url.pathname + url.search);
      if (url.pathname === '/api/v1/folders') return Promise.resolve(json({ folders: FOLDERS }));
      if (url.pathname.endsWith('/messages')) {
        const body = server.pages(url);
        return Promise.resolve(body instanceof Response ? body : json(body));
      }
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
  return server;
}

function renderPane(url = '/mail/inbox', slug = 'inbox', messageId?: string) {
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: <MessageListPane folderSlug={slug} messageId={messageId} />,
      },
    ],
    { initialEntries: [url] },
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

/** jsdom has no layout: the list box is 800 px high, a row is 96 px, a heading 36 px. */
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
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('MessageListPane', () => {
  it('shows the title, the unread count, search, filters and the rows with their texts', async () => {
    mockServer(() => ({ items: [message(1), message(2)], next: null, total: 2 }));
    renderPane();

    await waitFor(() =>
      expect(screen.getByRole('heading', { name: /Входящие/u })).toHaveTextContent(
        '4 непрочитанных',
      ),
    );
    expect(screen.getByRole('searchbox', { name: 'Поиск в почте' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Все' })).toHaveAttribute('aria-pressed', 'true');
    expect(await screen.findByRole('heading', { level: 2, name: 'Сегодня' })).toBeInTheDocument();
    expect(screen.getAllByRole('link')).toHaveLength(2);
    expect(screen.getByText('Sender 1')).toBeInTheDocument();
    expect(screen.getByText('Subject 1')).toBeInTheDocument();
    expect(screen.getByText('Preview 1')).toBeInTheDocument();
  });

  it('marks unread, flagged and with-attachments messages, and the selected row', async () => {
    mockServer(() => ({
      items: [
        message(1, { seen: false, flagged: true, hasAttachments: true, labels: ['Work'] }),
        message(2),
      ],
      next: null,
      total: 2,
    }));
    renderPane('/mail/inbox', 'inbox', 'm2');

    const rows = await screen.findAllByRole('link');
    const first = within(rows[0]!);
    expect(first.getByRole('img', { name: 'Не прочитано' })).toBeInTheDocument();
    expect(first.getByRole('img', { name: 'С флажком' })).toBeInTheDocument();
    expect(first.getByRole('img', { name: 'Есть вложения' })).toBeInTheDocument();
    expect(first.getByText('Work')).toBeInTheDocument();
    expect(rows[0]).not.toHaveAttribute('aria-current');
    expect(within(rows[1]!).queryByRole('img', { name: 'Не прочитано' })).not.toBeInTheDocument();
    expect(rows[1]).toHaveAttribute('aria-current', 'true');
  });

  it('links each row to the message in the address, keeping the filter', async () => {
    mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    renderPane('/mail/inbox?filter=unread');

    const row = await screen.findByRole('link');
    expect(row).toHaveAttribute('href', '/mail/inbox/m1?filter=unread');
  });

  it('shows the recipient instead of the sender in the sent folder', async () => {
    mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    renderPane('/mail/sent', 'sent');

    expect(await screen.findByText('Кому: to@example.org')).toBeInTheDocument();
  });

  it('writes a placeholder for a message without subject or sender', async () => {
    mockServer(() => ({ items: [message(1, { subject: '', from: null })], next: null, total: 1 }));
    renderPane();

    expect(await screen.findByText('(без темы)')).toBeInTheDocument();
    expect(screen.getByText('Без отправителя')).toBeInTheDocument();
  });

  it('shows message texts as plain text, never as markup', async () => {
    mockServer(() => ({
      items: [message(1, { subject: '<img src=x onerror=alert(1)>', preview: '<b>bold</b>' })],
      next: null,
      total: 1,
    }));
    renderPane();

    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img[src="x"]')).toBeNull();
    expect(document.querySelector('b')).toBeNull();
  });

  it('asks for the folder by its path, and for the filter the chip stands for', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    const user = userEvent.setup();
    const router = renderPane('/mail/Работа%2FОтчёты', 'Работа/Отчёты');
    await screen.findByRole('heading', { level: 2, name: 'Сегодня' });
    expect(server.urls.some((u) => u.startsWith('/api/v1/folders/%D0%A0'))).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Непрочитанные' }));

    await waitFor(() => expect(server.urls.at(-1)).toContain('filter=unread'));
    expect(router.state.location.search).toBe('?filter=unread');
    expect(screen.getByRole('button', { name: 'Непрочитанные' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('takes the flagged view from the address', async () => {
    const server = mockServer(() => ({ items: [], next: null, total: 0 }));
    renderPane('/mail/inbox?filter=flagged');

    expect(await screen.findByText('Здесь пусто. Попробуйте другой фильтр.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'С флажком' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(server.urls.some((u) => u.includes('filter=flagged'))).toBe(true);
  });

  it('loads the next page by itself when the end of the list is near, with the cursor, and stops on the last page', async () => {
    const server = mockServer((url) =>
      url.searchParams.get('after') === '1.5'
        ? { items: [message(3)], next: null, total: 3 }
        : { items: [message(1), message(2)], next: '1.5', total: 3 },
    );
    renderPane();

    expect(await screen.findByText('Subject 3')).toBeInTheDocument();
    expect(server.urls.filter((u) => u.includes('/messages'))).toHaveLength(2);
    expect(server.urls.at(-1)).toContain('after=1.5');
    expect(screen.getAllByRole('link')).toHaveLength(3);
  });

  it('says in plain words that the list could not be loaded and tries again on request', async () => {
    let fail = true;
    mockServer(() =>
      fail
        ? json({ code: 'mail_unavailable', title: 'x' }, { status: 503 })
        : { items: [message(1)], next: null, total: 1 },
    );
    const user = userEvent.setup();
    renderPane();

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Повторить' }));
    expect(await screen.findByText('Subject 1')).toBeInTheDocument();
  });

  it('says so when the folder in the address does not exist', async () => {
    mockServer(() => ({ items: [], next: null, total: 0 }));
    renderPane('/mail/nope', 'nope');

    expect(await screen.findByText('Такой папки нет.')).toBeInTheDocument();
  });

  it('refreshes on the button', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');
    const before = server.urls.filter((u) => u.includes('/messages')).length;

    await user.click(screen.getByRole('button', { name: 'Обновить' }));

    await waitFor(() =>
      expect(server.urls.filter((u) => u.includes('/messages')).length).toBeGreaterThan(before),
    );
  });
});

describe('offline', () => {
  it('shows the banner while the browser is offline and keeps the loaded list', async () => {
    const online = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    renderPane();

    expect(await screen.findByText('Subject 1')).toBeInTheDocument();
    expect(screen.getByText(/Нет связи/u)).toBeInTheDocument();
    online.mockRestore();
  });
});

describe('a long list', () => {
  const many = (from: number, count: number) =>
    Array.from({ length: count }, (_, i) => message(from + i));

  it('keeps only the rows near the visible part in the page, however many there are', async () => {
    mockServer(() => ({ items: many(1, 50), next: '1.50', total: 50000 }));
    renderPane();

    await screen.findByText('Subject 1');
    const rendered = screen.getAllByRole('link').length;
    expect(rendered).toBeGreaterThan(5);
    expect(rendered).toBeLessThan(30);
  });

  it('asks for the next page when the user scrolls to the end of what is loaded', async () => {
    const server = mockServer((url) => {
      const after = url.searchParams.get('after');
      const from = after ? Number(after.split('.')[1]) + 1 : 1;
      return { items: many(from, 50), next: `1.${from + 49}`, total: 50000 };
    });
    renderPane();
    await screen.findByText('Subject 1');
    const pages = () => server.urls.filter((u) => u.includes('/messages')).length;
    expect(pages()).toBe(1); // the end is far away: nothing is loaded ahead of need

    const scroller = document.querySelector<HTMLElement>('.overflow-y-auto')!;
    scroller.scrollTop = 50 * 96;
    act(() => void scroller.dispatchEvent(new Event('scroll')));

    await waitFor(() => expect(pages()).toBe(2));
    expect(server.urls.at(-1)).toContain('after=1.50');
  });
});

describe('date groups in the list', () => {
  it('puts the messages under headings of their days, in the order the server gave them', async () => {
    const day = 24 * 60 * 60 * 1000;
    const at = (daysBack: number) => new Date(Date.now() - daysBack * day).toISOString();
    mockServer(() => ({
      items: [
        message(1),
        message(2, { date: at(1) }),
        message(3, { date: at(3) }),
        message(4, { date: at(40) }),
      ],
      next: null,
      total: 4,
    }));
    renderPane();

    await screen.findByRole('heading', { level: 2, name: 'Сегодня' });
    const headings = screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent);
    expect(headings.slice(0, 3)).toEqual(['Сегодня', 'Вчера', 'Ранее на неделе']);
    expect(headings).toHaveLength(4); // and the month of the oldest one
    expect(screen.getByText('Subject 2')).toBeInTheDocument();
  });
});

describe('labelColor', () => {
  it('gives a label the same color every time, one of the three of the design', () => {
    expect(labelColor('Work')).toBe(labelColor('Work'));
    expect(['work', 'bills', 'personal']).toContain(labelColor('Anything'));
  });
});
