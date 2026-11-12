import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { AppShell } from './AppShell';
import { MessageListPane } from './MessageListPane';
import { labelColor } from '@/shared/lib';
import { ToastProvider } from '@/shared/toast';

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
  /** What the settings endpoint answers; when absent the endpoint does not exist (404). */
  settings?: { density: string; language: string | null; labelColors: Record<string, string> };
  /** Bodies of the changes of settings that were sent. */
  patches: unknown[];
  /** Makes the next change of settings fail. */
  failPatch?: boolean;
  /** Bodies of the batch actions that were sent. */
  batches: { ids: string[]; action: string }[];
  /** Makes the batch actions fail. */
  failBatch?: boolean;
  /** Holds the answer to the batch actions back until it is released. */
  hold?: Promise<void>;
  /** What a successful batch answers (the default moves nothing that can be found again). */
  batchResult?: (body: { ids: string[]; action: string }) => unknown;
}

function mockServer(pages: (url: URL) => unknown): Server {
  const server: Server = { urls: [], pages, patches: [], batches: [] };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = new URL(String(input), 'http://localhost');
      server.urls.push(url.pathname + url.search);
      if (url.pathname === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (url.pathname === '/api/v1/settings' && server.settings) {
        if (init?.method === 'PATCH') {
          server.patches.push(JSON.parse(String(init.body)));
          if (server.failPatch) {
            return Promise.resolve(json({ code: 'internal_error', title: 'x' }, { status: 500 }));
          }
          const { density } = JSON.parse(String(init.body)) as { density: string | null };
          server.settings = { ...server.settings, density: density ?? server.settings.density };
        }
        return Promise.resolve(json(server.settings));
      }
      if (url.pathname === '/api/v1/messages/batch') {
        const body = JSON.parse(String(init?.body)) as { ids: string[]; action: string };
        server.batches.push(body);
        return (server.hold ?? Promise.resolve()).then(() => {
          if (server.failBatch) {
            return json({ code: 'mail_unavailable', title: 'x' }, { status: 503 });
          }
          const answer = server.batchResult?.(body);
          if (answer instanceof Response) return answer;
          return json(answer ?? { changed: body.ids.length, folder: 'Trash', moved: [] });
        });
      }
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
        element: (
          <AppShell
            messageOpen={false}
            nav={<p>folders</p>}
            list={
              <MessageListPane
                folderSlug={slug}
                messageId={messageId}
                address="alice@vinette.test"
              />
            }
            reader="reader"
          />
        ),
      },
    ],
    { initialEntries: [url] },
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <ToastProvider>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </ToastProvider>,
  );
  return router;
}

/** jsdom has no layout: the list box is 800 px high, a row is 96 px, a heading 36 px. */
function setWidth(width: number) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: width >= Number(/min-width: (\d+)px/.exec(query)?.[1]),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

beforeEach(() => {
  setWidth(1400);
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

describe('mobile and tablet', () => {
  beforeEach(() => setWidth(400));

  it('has the header of the phone: menu, title, search button, account; the search box opens on demand', async () => {
    mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    const user = userEvent.setup();
    renderPane();

    await screen.findByText('Subject 1');
    expect(screen.getByRole('button', { name: 'Меню' })).toBeInTheDocument();
    expect(
      screen.getByRole('img', { name: 'alice@vinette.test', hidden: true }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Обновить' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Поиск в почте' }));

    expect(screen.getByRole('searchbox', { name: 'Поиск в почте' })).toBeInTheDocument();
  });

  it('opens the folders from the menu button', async () => {
    mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    const user = userEvent.setup();
    renderPane();

    await user.click(await screen.findByRole('button', { name: 'Меню' }));

    expect(screen.getByRole('navigation', { name: 'Папки' })).toBeVisible();
  });

  it('shows rows with an avatar of the sender and the floating "Compose" button', async () => {
    mockServer(() => ({ items: [message(1, { seen: false })], next: null, total: 1 }));
    const user = userEvent.setup();
    const router = renderPane();

    const row = await screen.findByRole('link');
    expect(within(row).getByRole('img', { name: 'Не прочитано' })).toBeInTheDocument();
    expect(within(row).getByText('Sender 1')).toBeInTheDocument();
    expect(within(row).getByText('S1')).toBeInTheDocument(); // the avatar's initials
    await user.click(screen.getByRole('button', { name: 'Написать' }));
    expect(router.state.location.pathname).toBe('/compose');
  });

  it('on a desktop and a tablet there is no floating button and the search box is always there', async () => {
    mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    setWidth(900);
    renderPane();

    await screen.findByText('Subject 1');
    expect(screen.queryByRole('button', { name: 'Написать' })).not.toBeInTheDocument();
    expect(screen.getByRole('searchbox')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Обновить' })).toBeInTheDocument();
  });
});

describe('list density', () => {
  const settings = (density: string) => ({ density, language: null, labelColors: {} });

  it('shows the preview and asks for it in the comfortable density', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    server.settings = settings('comfortable');
    renderPane();

    expect(await screen.findByText('Preview 1')).toBeInTheDocument();
    expect(
      server.urls.filter((u) => u.includes('/messages')).every((u) => !u.includes('preview=false')),
    ).toBe(true);
  });

  it('shows no preview in the compact density and does not ask the server for it', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    server.settings = settings('compact');
    renderPane();

    expect(await screen.findByText('Subject 1')).toBeInTheDocument();
    expect(screen.queryByText('Preview 1')).not.toBeInTheDocument();
    const requests = server.urls.filter((u) => u.includes('/messages'));
    expect(requests).toHaveLength(1); // the list waited for the settings: no request for previews first
    expect(requests[0]).toContain('preview=false');
  });

  it('switches from the options panel at once, saves the choice and asks for previews again when going back', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    server.settings = settings('comfortable');
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Preview 1');

    await user.click(screen.getByRole('button', { name: 'Параметры списка' }));
    await user.click(screen.getByRole('button', { name: 'Компактная' }));

    await waitFor(() => expect(screen.queryByText('Preview 1')).not.toBeInTheDocument());
    expect(server.patches).toEqual([{ density: 'compact', language: null, labelColors: null }]);
    expect(screen.queryByRole('group', { name: 'Плотность списка' })).not.toBeInTheDocument(); // the panel closed

    await user.click(screen.getByRole('button', { name: 'Параметры списка' }));
    expect(screen.getByRole('button', { name: 'Компактная' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await user.click(screen.getByRole('button', { name: 'Обычная' }));
    expect(await screen.findByText('Preview 1')).toBeInTheDocument();
  });

  it('goes back to the old choice when the server refuses to save', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    server.settings = settings('comfortable');
    server.failPatch = true;
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Preview 1');

    await user.click(screen.getByRole('button', { name: 'Параметры списка' }));
    await user.click(screen.getByRole('button', { name: 'Компактная' }));

    await waitFor(() => expect(server.patches).toHaveLength(1));
    expect(await screen.findByText('Preview 1')).toBeInTheDocument();
  });

  it('keeps the comfortable list when the settings cannot be read', async () => {
    mockServer(() => ({ items: [message(1)], next: null, total: 1 })); // no settings endpoint
    renderPane();

    expect(await screen.findByText('Preview 1')).toBeInTheDocument();
  });

  it('the options panel closes on Escape and on a click outside', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    server.settings = settings('comfortable');
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Preview 1');

    await user.click(screen.getByRole('button', { name: 'Параметры списка' }));
    expect(screen.getByRole('group', { name: 'Плотность списка' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Плотность списка' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Параметры списка' }));
    await user.click(document.body);
    expect(screen.queryByRole('group', { name: 'Плотность списка' })).not.toBeInTheDocument();
  });

  it('a phone keeps the preview and has no options button', async () => {
    const server = mockServer(() => ({ items: [message(1)], next: null, total: 1 }));
    server.settings = settings('compact');
    setWidth(400);
    renderPane();

    expect(await screen.findByText('Preview 1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Параметры списка' })).not.toBeInTheDocument();
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

    const scroller = document.querySelector<HTMLElement>('[aria-busy]')!;
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

describe('selecting messages', () => {
  const page = (count: number) => ({
    items: Array.from({ length: count }, (_, i) => message(i + 1)),
    next: null,
  });
  const box = (n: number) =>
    screen.getByRole('checkbox', { name: `Выбрать письмо «Subject ${n}»` });

  it('shows the list as before while nothing is selected', async () => {
    mockServer(() => page(3));
    renderPane();
    await screen.findByText('Subject 1');

    expect(
      screen.queryByRole('toolbar', { name: 'Действия с выбранными письмами' }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: /Входящие/ })).toBeVisible();
  });

  it('a box selects a message, and the heading gives way to the actions', async () => {
    mockServer(() => page(3));
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');

    await user.click(box(2));

    const bar = screen.getByRole('toolbar', { name: 'Действия с выбранными письмами' });
    expect(within(bar).getByText('Выбрано: 1')).toBeInTheDocument();
    expect(box(2)).toBeChecked();
    expect(within(bar).getByRole('button', { name: 'В архив' })).toBeInTheDocument();
    expect(within(bar).getByRole('button', { name: 'В спам' })).toBeInTheDocument();
    expect(within(bar).getByRole('button', { name: 'Удалить' })).toBeInTheDocument();
    expect(within(bar).getByRole('button', { name: 'Отметить прочитанными' })).toBeInTheDocument();
  });

  it('Ctrl and a click select a row instead of opening it; Shift and a click select the range', async () => {
    mockServer(() => page(5));
    const user = userEvent.setup();
    const router = renderPane();
    await screen.findByText('Subject 1');

    await user.keyboard('{Control>}');
    await user.click(screen.getByText('Subject 2'));
    await user.keyboard('{/Control}');
    await user.keyboard('{Shift>}');
    await user.click(screen.getByText('Subject 4'));
    await user.keyboard('{/Shift}');

    expect(router.state.location.pathname).toBe('/mail/inbox'); // nothing was opened
    expect([1, 2, 3, 4, 5].map((n) => (box(n) as HTMLInputElement).checked)).toEqual([
      false,
      true,
      true,
      true,
      false,
    ]);
    expect(screen.getByText('Выбрано: 3')).toBeInTheDocument();
  });

  it('a plain click still opens the message', async () => {
    mockServer(() => page(2));
    const user = userEvent.setup();
    const router = renderPane();

    await user.click(await screen.findByText('Subject 2'));

    expect(router.state.location.pathname).toBe('/mail/inbox/m2');
  });

  it('selects all the loaded messages and clears the selection', async () => {
    mockServer(() => page(4));
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');

    await user.click(box(1));
    await user.click(screen.getByRole('button', { name: 'Выбрать все' }));
    expect(screen.getByText('Выбрано: 4')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Выбрать все' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Снять выбор' }));
    expect(screen.queryByRole('toolbar')).not.toBeInTheDocument();
    expect(box(1)).not.toBeChecked();
  });

  it('selects at most 100 messages and says so', async () => {
    mockServer(() => page(120));
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');

    await user.click(box(1));
    await user.click(screen.getByRole('button', { name: 'Выбрать все' }));

    expect(screen.getByText('Выбрано: 100')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent(
      'Можно выбрать не больше 100 писем за раз.',
    );
  });

  it('applies an action to the selected messages in the order of the list, and the selection ends', async () => {
    const server = mockServer(() => page(4));
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');

    await user.click(box(3));
    await user.click(box(1));
    await user.click(screen.getByRole('button', { name: 'Удалить' }));

    await waitFor(() =>
      expect(server.batches).toEqual([{ ids: ['m1', 'm3'], action: 'trash', to: null }]),
    );
    await waitFor(() => expect(screen.queryByRole('toolbar')).not.toBeInTheDocument());
    expect(server.urls.filter((u) => u.endsWith('/messages?limit=50')).length).toBeGreaterThan(1); // the list is loaded again
  });

  it('leaves the open message when it was moved away', async () => {
    mockServer(() => page(2));
    const user = userEvent.setup();
    const router = renderPane('/mail/inbox/m1', 'inbox', 'm1');
    await screen.findByText('Subject 1');

    await user.click(box(1));
    await user.click(screen.getByRole('button', { name: 'В архив' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
  });

  it('takes the rows out of the list at once, before the server has answered', async () => {
    const server = mockServer(() => page(3));
    let release: () => void = () => undefined;
    server.hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');

    await user.click(box(1));
    await user.click(box(3));
    await user.click(screen.getByRole('button', { name: 'Удалить' }));

    await waitFor(() => expect(screen.queryByText('Subject 1')).not.toBeInTheDocument());
    expect(screen.queryByText('Subject 3')).not.toBeInTheDocument();
    expect(screen.getByText('Subject 2')).toBeInTheDocument();
    expect(screen.queryByRole('toolbar')).not.toBeInTheDocument(); // and the selection is over
    release();
  });

  it('puts the rows and the selection back and says so in a toast when the server refuses', async () => {
    const server = mockServer(() => page(3));
    server.failBatch = true;
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');

    await user.click(box(1));
    await user.click(box(2));
    await user.click(screen.getByRole('button', { name: 'В спам' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
    await waitFor(() => expect(screen.getByText('Subject 1')).toBeInTheDocument());
    expect(screen.getByText('Subject 2')).toBeInTheDocument();
    expect(box(1)).toBeChecked();
    expect(box(2)).toBeChecked();
    expect(screen.getByText('Выбрано: 2')).toBeInTheDocument();
  });

  it('marks as read at once in the list, and the unread count of the folder follows', async () => {
    const server = mockServer(() => ({
      // real ids (the folder is in them): the counter of the folder is found by it
      items: [
        message(1, { seen: false, id: 'SU5CT1g.1.1' }),
        message(2, { seen: false, id: 'SU5CT1g.1.2' }),
      ],
      next: null,
    }));
    let release: () => void = () => undefined;
    server.hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const user = userEvent.setup();
    renderPane();
    await screen.findByText('Subject 1');
    expect(screen.getAllByRole('img', { name: 'Не прочитано' })).toHaveLength(2);

    await user.click(box(1));
    await user.click(screen.getByRole('button', { name: 'Отметить прочитанными' }));

    await waitFor(() =>
      expect(screen.getAllByRole('img', { name: 'Не прочитано' })).toHaveLength(1),
    );
    expect(screen.getByText('3 непрочитанных')).toBeInTheDocument(); // 4 unread in the folder, minus the one
    release();
  });

  describe('undoing a move', () => {
    const real = (count: number) => ({
      items: Array.from({ length: count }, (_, i) => message(i + 1, { id: `SU5CT1g.1.${i + 1}` })),
      next: null,
    });
    /** The server moved the messages to "Trash": their new ids are `VHJhc2g.5.<uid>`. */
    const moved = (body: { ids: string[]; action: string }) => ({
      changed: body.ids.length,
      folder: 'Trash',
      moved: body.ids.map((from, i) => ({ from, id: `VHJhc2g.5.${i + 10}` })),
    });

    it('offers "Undo" after a move, and undoing moves the messages back where they were', async () => {
      const server = mockServer(() => real(3));
      server.batchResult = moved;
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Subject 1');

      await user.click(box(1));
      await user.click(box(2));
      await user.click(screen.getByRole('button', { name: 'Удалить' }));

      const toast = await screen.findByRole('status');
      expect(toast).toHaveTextContent('2 письма перенесены в корзину');
      await user.click(within(toast).getByRole('button', { name: 'Отменить' }));

      await waitFor(() => expect(server.batches).toHaveLength(2));
      expect(server.batches[1]).toEqual({
        ids: ['VHJhc2g.5.10', 'VHJhc2g.5.11'],
        action: 'move',
        to: 'INBOX',
      });
      await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument());
    });

    it('asks for the list again after undoing, so the messages are back on screen', async () => {
      const server = mockServer(() => real(2));
      server.batchResult = moved;
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Subject 1');
      await user.click(box(1));
      await user.click(screen.getByRole('button', { name: 'В архив' }));
      const before = server.urls.filter((u) => u.includes('/messages?')).length;

      await user.click(await screen.findByRole('button', { name: 'Отменить' }));

      await waitFor(() =>
        expect(server.urls.filter((u) => u.includes('/messages?')).length).toBeGreaterThan(before),
      );
    });

    it.each([
      ['В архив', 'Письмо перенесено в архив'],
      ['В спам', 'Письмо перенесено в спам'],
      ['Удалить', 'Письмо перенесено в корзину'],
    ])('after "%s" the toast says "%s"', async (name, text) => {
      const server = mockServer(() => real(2));
      server.batchResult = moved;
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Subject 1');

      await user.click(box(1));
      await user.click(screen.getByRole('button', { name }));

      expect(await screen.findByRole('status')).toHaveTextContent(text);
    });

    it('says what happened but has no "Undo" when the server does not tell where the messages went', async () => {
      mockServer(() => real(2)); // the default answer has no new ids
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Subject 1');

      await user.click(box(1));
      await user.click(screen.getByRole('button', { name: 'Удалить' }));

      expect(await screen.findByRole('status')).toHaveTextContent('Письмо перенесено в корзину');
      expect(screen.queryByRole('button', { name: 'Отменить' })).not.toBeInTheDocument();
    });

    it('offers nothing to undo after marking as read', async () => {
      mockServer(() => real(2));
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Subject 1');

      await user.click(box(1));
      await user.click(screen.getByRole('button', { name: 'Отметить прочитанными' }));

      await waitFor(() => expect(screen.queryByRole('toolbar')).not.toBeInTheDocument());
      expect(screen.queryByRole('status')).not.toBeInTheDocument();
    });

    it('says in plain words when putting the messages back did not work', async () => {
      const server = mockServer(() => real(2));
      server.batchResult = (body) =>
        body.action === 'move'
          ? new Response(JSON.stringify({ code: 'mail_unavailable', title: 'x' }), {
              status: 503,
              headers: { 'Content-Type': 'application/json' },
            })
          : moved(body);
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Subject 1');
      await user.click(box(1));
      await user.click(screen.getByRole('button', { name: 'Удалить' }));

      await user.click(await screen.findByRole('button', { name: 'Отменить' }));

      expect(await screen.findByRole('alert')).toHaveTextContent('Не удалось вернуть письмо');
    });
  });

  it('does not offer to move messages to the folder they are in', async () => {
    mockServer(() => page(2));
    const user = userEvent.setup();
    renderPane('/mail/sent', 'sent');
    await screen.findByText('Subject 1');
    await user.click(box(1));

    expect(screen.getByRole('button', { name: 'В архив' })).toBeInTheDocument();
  });

  it('on a phone a long press selects a row, and then a tap selects instead of opening', async () => {
    setWidth(500);
    mockServer(() => page(3));
    const router = renderPane();
    const row = (await screen.findByText('Subject 1')).closest('a')!;

    fireEvent.pointerDown(row, { pointerType: 'touch' });
    await act(() => new Promise((resolve) => setTimeout(resolve, 600)));
    fireEvent.pointerUp(row, { pointerType: 'touch' });
    fireEvent.click(row); // the click that ends the long press does not open the message

    expect(screen.getByText('Выбрано: 1')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/mail/inbox');

    fireEvent.click(screen.getByText('Subject 2').closest('a')!);

    expect(screen.getByText('Выбрано: 2')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/mail/inbox');
  });

  it('on a phone a short tap opens the message', async () => {
    setWidth(500);
    mockServer(() => page(2));
    const router = renderPane();
    const row = (await screen.findByText('Subject 1')).closest('a')!;

    fireEvent.pointerDown(row, { pointerType: 'touch' });
    fireEvent.pointerUp(row, { pointerType: 'touch' });
    fireEvent.click(row);

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox/m1'));
    expect(screen.queryByText(/Выбрано/)).not.toBeInTheDocument();
  });

  it('starts with nothing selected in another folder', async () => {
    mockServer(() => page(2));
    const user = userEvent.setup();
    const router = renderPane();
    await screen.findByText('Subject 1');
    await user.click(box(1));

    await act(() => router.navigate('/mail/inbox?filter=unread'));

    await waitFor(() => expect(screen.queryByRole('toolbar')).not.toBeInTheDocument());
  });
});
