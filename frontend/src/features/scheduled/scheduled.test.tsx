import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider, useLocation } from 'react-router';
import { ToastProvider } from '@/shared/toast';
import { ScheduledDetail } from './ScheduledDetail';
import { ScheduledList } from './ScheduledList';
import { ScheduledWatcher } from './ScheduledWatcher';
import { summaryOf } from './api';
import { fullWhen, shortWhen } from './when';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const item = (id: string, extra: Record<string, unknown> = {}) => ({
  id,
  sendAt: '2099-01-05T08:00:00Z',
  subject: `Тема ${id}`,
  to: [{ name: 'Анна', address: 'anna@example.org' }],
  cc: [],
  state: 'pending',
  problem: null,
  inDrafts: false,
  ...extra,
});

function mockApi(items: unknown[], calls: string[] = []) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push(`${method} ${url}`);
      if (url === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      if (url === '/api/v1/scheduled' && method === 'GET') return Promise.resolve(json({ items }));
      if (url.startsWith('/api/v1/scheduled/'))
        return Promise.resolve(new Response(null, { status: 204 }));
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
}

function Where() {
  return <output data-testid="where">{useLocation().pathname}</output>;
}

function show(ui: React.ReactNode, path = '/mail/@scheduled') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: (
          <QueryClientProvider client={client}>
            <ToastProvider>
              {ui}
              <Where />
            </ToastProvider>
          </QueryClientProvider>
        ),
      },
    ],
    { initialEntries: [path] },
  );
  return render(<RouterProvider router={router} />);
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('when', () => {
  const now = new Date(2026, 10, 20, 12, 0);
  const words = { today: 'сегодня', tomorrow: 'завтра' };

  it('says today and tomorrow in words, then the weekday, then the date', () => {
    expect(shortWhen(new Date(2026, 10, 20, 18, 0), now, 'ru', words)).toMatch(/^сегодня, 18:00/);
    expect(shortWhen(new Date(2026, 10, 21, 8, 0), now, 'ru', words)).toMatch(/^завтра, 08:00/);
    expect(shortWhen(new Date(2026, 10, 24, 8, 0), now, 'ru', words)).toMatch(/^вт, 08:00/);
    expect(shortWhen(new Date(2026, 11, 5, 8, 0), now, 'ru', words)).toMatch(/5 дек/);
    expect(fullWhen(new Date(2026, 10, 24, 8, 0), 'ru')).toMatch(/24 нояб/);
  });
});

describe('summaryOf', () => {
  it('counts the waiting and the failed', () => {
    expect(summaryOf([item('a'), item('b', { state: 'failed' }), item('c')] as never)).toEqual({
      waiting: 2,
      failed: 1,
    });
    expect(summaryOf(undefined)).toEqual({ waiting: 0, failed: 0 });
  });
});

describe('ScheduledList', () => {
  it('puts the ones that did not go first, with the reason in words', async () => {
    mockApi([item('a'), item('b', { state: 'failed', problem: 'recipient_rejected' })]);
    show(<ScheduledList />);

    expect(await screen.findByText('Почта не приняла одного из получателей.')).toBeInTheDocument();
    const groups = screen.getAllByRole('region').map((g) => g.getAttribute('aria-label'));
    expect(groups).toEqual(['Не ушли', 'Ждут отправки']);
    expect(screen.getByText('1 ждёт отправки')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Тема a/ })).toHaveAttribute(
      'href',
      '/mail/@scheduled/a',
    );
  });

  it('says what to do when nothing is scheduled', async () => {
    mockApi([]);
    show(<ScheduledList />);
    expect(await screen.findByText(/Отложенных писем нет/)).toBeInTheDocument();
  });
});

describe('ScheduledDetail', () => {
  it('cancels a waiting message: it leaves the queue and the list is shown', async () => {
    const calls: string[] = [];
    mockApi([item('a')], calls);
    show(<ScheduledDetail id="a" />, '/mail/@scheduled/a');

    await userEvent.click(await screen.findByRole('button', { name: 'Отменить отправку' }));

    await waitFor(() => expect(calls).toContain('DELETE /api/v1/scheduled/a'));
    await waitFor(() =>
      expect(screen.getByTestId('where')).toHaveTextContent(/^\/mail\/@scheduled$/),
    );
  });

  it('shows why one did not go, with the way to the drafts and to remove the note', async () => {
    mockApi([item('a', { state: 'failed', problem: 'session_revoked', inDrafts: true })]);
    show(<ScheduledDetail id="a" />, '/mail/@scheduled/a');

    expect(await screen.findByText(/Не удалось войти в почту/)).toBeInTheDocument();
    expect(screen.getByText('Письмо лежит в «Черновиках».')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Открыть «Черновики»' })).toHaveAttribute(
      'href',
      '/mail/drafts',
    );
    expect(screen.getByRole('button', { name: 'Убрать отметку' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Отменить отправку' })).toBeNull();
  });

  it('says so when the message is no longer in the queue', async () => {
    mockApi([]);
    show(<ScheduledDetail id="gone" />, '/mail/@scheduled/gone');
    expect(await screen.findByRole('alert')).toHaveTextContent(/уже нет в очереди/);
  });
});

describe('ScheduledWatcher', () => {
  it('tells about a failure once and leads to the list', async () => {
    mockApi([item('a', { state: 'failed', problem: 'send_refused' }), item('b')]);
    show(<ScheduledWatcher />, '/mail/inbox');

    expect(await screen.findByText('Не отправилось отложенное письмо')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Посмотреть' }));
    expect(screen.getByTestId('where')).toHaveTextContent('/mail/@scheduled');
  });

  it('does not tell again about a failure it already told about', async () => {
    sessionStorage.setItem('vinette.scheduled.told', JSON.stringify(['a']));
    const calls: string[] = [];
    mockApi([item('a', { state: 'failed', problem: 'send_refused' })], calls);
    show(<ScheduledWatcher />, '/mail/inbox');

    await waitFor(() => expect(calls).toContain('GET /api/v1/scheduled'));
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByText('Не отправилось отложенное письмо')).toBeNull();
  });
});
