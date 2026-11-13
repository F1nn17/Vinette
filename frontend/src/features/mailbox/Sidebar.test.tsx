import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { ToastProvider } from '@/shared/toast';
import { AppShell } from './AppShell';
import { Sidebar } from './Sidebar';
import { DESKTOP_MIN_WIDTH } from './use-layout';

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
  folder('INBOX', 'inbox', { unread: 4, total: 50 }),
  folder('Drafts', 'drafts', { total: 2, unread: 0 }),
  folder('Sent', 'sent'),
  folder('Archive', 'archive'),
  folder('Junk', 'junk', { unread: 3 }),
  folder('Trash', 'trash'),
  folder('Работа', 'other', { name: 'Работа' }),
  folder('Hidden', 'other', { subscribed: false }),
  folder('Container', 'other', { canContainMessages: false }),
];

const GB = 1024 ** 3;

function mockApi(options: { folders?: () => Response; storage?: unknown } = {}) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = String(input);
      if (url === '/api/v1/folders')
        return Promise.resolve(options.folders?.() ?? json({ folders: FOLDERS }));
      if (url === '/api/v1/labels') return Promise.resolve(json({ labels: [] }));
      if (url === '/api/v1/quota')
        return Promise.resolve(
          json({
            storage:
              'storage' in options ? options.storage : { usedBytes: 2.1 * GB, limitBytes: 10 * GB },
          }),
        );
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
}

function setWidth(width: number) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: width >= Number(/min-width: (\d+)px/.exec(query)?.[1]),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

function renderSidebar(url = '/mail/inbox', slug = 'inbox') {
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: (
          <AppShell
            messageOpen={false}
            nav={<Sidebar address="alice@vinette.test" folderSlug={slug} />}
            list="list"
            reader="reader"
          />
        ),
      },
    ],
    { initialEntries: [url] },
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryClientProvider>,
  );
  return router;
}

const nav = () => screen.getByRole('navigation', { name: 'Папки' });

beforeEach(() => setWidth(DESKTOP_MIN_WIDTH));
afterEach(() => vi.unstubAllGlobals());

describe('Sidebar', () => {
  it('shows the folders in the interface language with counters, only the ones that can be opened', async () => {
    mockApi();
    renderSidebar();

    const inbox = await within(nav()).findByRole('link', { name: /Входящие/u });
    expect(inbox).toHaveTextContent('4');
    expect(within(nav()).getByRole('link', { name: /Черновики/u })).toHaveTextContent('2'); // drafts: how many there are
    expect(within(nav()).getByRole('link', { name: /Спам/u })).toHaveTextContent('3');
    expect(within(nav()).getByRole('link', { name: 'Отправленные' })).toBeInTheDocument();
    expect(within(nav()).getByRole('link', { name: 'Работа' })).toBeInTheDocument(); // the user's own keep their name
    expect(within(nav()).queryByText('Hidden')).not.toBeInTheDocument();
    expect(within(nav()).queryByText('Container')).not.toBeInTheDocument();
  });

  it('puts "Flagged" right after the inbox', async () => {
    mockApi();
    renderSidebar();

    await within(nav()).findByRole('link', { name: /Входящие/u });
    const names = within(nav())
      .getAllByRole('link')
      .map((a) => a.textContent);
    expect(names.slice(0, 3)).toEqual(['Входящие4', 'С флажком', 'Черновики2']);
  });

  it('marks only the open folder as the current page', async () => {
    mockApi();
    renderSidebar('/mail/sent', 'sent');

    const sent = await within(nav()).findByRole('link', { name: 'Отправленные' });
    expect(sent).toHaveAttribute('aria-current', 'page');
    expect(within(nav()).getByRole('link', { name: /Входящие/u })).not.toHaveAttribute(
      'aria-current',
    );
  });

  it('marks "Flagged" and not the inbox when the flagged view is open', async () => {
    mockApi();
    renderSidebar('/mail/inbox?filter=flagged', 'inbox');

    expect(await within(nav()).findByRole('link', { name: 'С флажком' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(within(nav()).getByRole('link', { name: /Входящие/u })).not.toHaveAttribute(
      'aria-current',
    );
  });

  it('navigates by link and can be walked with the keyboard', async () => {
    mockApi();
    const user = userEvent.setup();
    const router = renderSidebar();
    await within(nav()).findByRole('link', { name: 'Отправленные' });

    await user.click(screen.getByRole('button', { name: 'Написать' }));
    expect(router.state.location.pathname).toBe('/compose');

    await router.navigate('/mail/inbox');
    screen.getByRole('link', { name: 'Отправленные' }).focus();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/Sent'.toLowerCase()));
  });

  it('shows the address, the (empty) labels section and the storage', async () => {
    mockApi();
    renderSidebar();

    expect(await screen.findByText('alice@vinette.test')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Метки' })).toBeInTheDocument();
    const bar = await screen.findByRole('progressbar', { name: 'Занятое место в почте' });
    expect(bar).toHaveAttribute('aria-valuemax', String(10 * GB));
    expect(screen.getByText(/Занято 2,1/u)).toBeInTheDocument();
  });

  it('leaves the storage out when the mail provider does not report it', async () => {
    mockApi({ storage: null });
    renderSidebar();

    await within(nav()).findByRole('link', { name: /Входящие/u });
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('says in plain words that folders could not be loaded and offers to try again', async () => {
    let fail = true;
    mockApi({
      folders: () =>
        fail
          ? json({ code: 'mail_unavailable', title: 'x' }, { status: 503 })
          : json({ folders: FOLDERS }),
    });
    const user = userEvent.setup();
    renderSidebar();

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Повторить' }));
    expect(await within(nav()).findByRole('link', { name: /Входящие/u })).toBeInTheDocument();
  });

  it('is a rail of icons on a tablet: names stay for screen readers, no labels or storage', async () => {
    setWidth(900);
    mockApi();
    renderSidebar();

    const inbox = await within(nav()).findByRole('link', { name: /Входящие/u });
    expect(inbox).toHaveAttribute('title', 'Входящие');
    expect(screen.getByRole('button', { name: 'Написать' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Метки' })).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.queryByText('alice@vinette.test')).not.toBeInTheDocument();
  });
});
