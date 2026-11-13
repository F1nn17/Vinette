import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { ToastProvider } from '@/shared/toast';
import { SidebarLabels } from './SidebarLabels';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

interface Call {
  method: string;
  path: string;
  body: unknown;
}

function mockApi(labels: { name: string; color: string | null }[], fail?: (call: Call) => boolean) {
  const calls: Call[] = [];
  // the server forgets a deleted label: the list read again no longer has it, and the row is gone
  const current = [...labels];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = init?.method ?? 'GET';
      if (url.pathname === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      const call: Call = {
        method,
        path: decodeURI(url.pathname),
        body: init?.body ? (JSON.parse(String(init.body)) as unknown) : undefined,
      };
      if (method !== 'GET') calls.push(call);
      if (fail?.(call))
        return Promise.resolve(
          json(
            { code: 'conflict', title: 'A label with this name already exists.' },
            { status: 409 },
          ),
        );
      if (url.pathname === '/api/v1/labels' && method === 'GET')
        return Promise.resolve(json({ labels: current }));
      if (method === 'DELETE') {
        const at = current.findIndex((l) => `/api/v1/labels/${l.name}` === call.path);
        if (at >= 0) current.splice(at, 1);
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      if (method === 'POST' || method === 'PATCH')
        return Promise.resolve(json({ name: 'x', color: null }));
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
  return calls;
}

function renderLabels(active?: string) {
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: <SidebarLabels activeLabel={active} onNavigate={() => undefined} />,
      },
    ],
    { initialEntries: ['/mail/inbox'] },
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

afterEach(() => vi.unstubAllGlobals());

describe('SidebarLabels', () => {
  it('says there are no labels yet', async () => {
    mockApi([]);
    renderLabels();

    expect(await screen.findByText('Меток пока нет.')).toBeInTheDocument();
  });

  it('lists the labels as links to their messages and marks the open one', async () => {
    mockApi([
      { name: 'Work', color: 'work' },
      { name: 'Счета 2026', color: null },
    ]);
    renderLabels('счета 2026');

    const work = await screen.findByRole('link', { name: 'Work' });
    const bills = screen.getByRole('link', { name: 'Счета 2026' });
    expect(work).toHaveAttribute('href', '/mail/@label/Work');
    expect(bills).toHaveAttribute('href', `/mail/@label/${encodeURIComponent('Счета 2026')}`);
    expect(bills).toHaveAttribute('aria-current', 'page');
    expect(work).not.toHaveAttribute('aria-current');
  });

  it('turns "+" into a field, and Enter makes the label', async () => {
    const user = userEvent.setup();
    const calls = mockApi([]);
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Новая метка' }));
    await user.type(screen.getByRole('textbox', { name: 'Название новой метки' }), 'Отпуск{Enter}');

    await waitFor(() =>
      expect(calls).toContainEqual({
        method: 'POST',
        path: '/api/v1/labels',
        body: { name: 'Отпуск', color: null },
      }),
    );
    expect(screen.queryByRole('textbox', { name: 'Название новой метки' })).not.toBeInTheDocument();
  });

  it('cancels a new label on Escape without asking the server', async () => {
    const user = userEvent.setup();
    const calls = mockApi([]);
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Новая метка' }));
    await user.type(
      screen.getByRole('textbox', { name: 'Название новой метки' }),
      'Отпуск{Escape}',
    );

    expect(screen.queryByRole('textbox', { name: 'Название новой метки' })).not.toBeInTheDocument();
    expect(calls).toEqual([]);
  });

  it('says in a toast, in plain words, when the label cannot be made', async () => {
    const user = userEvent.setup();
    mockApi([], (call) => call.method === 'POST');
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Новая метка' }));
    await user.type(screen.getByRole('textbox', { name: 'Название новой метки' }), 'Work{Enter}');

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('changes the color from the menu of the label', async () => {
    const user = userEvent.setup();
    const calls = mockApi([{ name: 'Work', color: 'work' }]);
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Действия с меткой «Work»' }));
    const colors = within(screen.getByRole('group', { name: 'Цвет метки' }));
    expect(colors.getByRole('button', { name: 'Работа' })).toHaveAttribute('aria-pressed', 'true');
    await user.click(colors.getByRole('button', { name: 'Личное' }));

    await waitFor(() =>
      expect(calls).toContainEqual({
        method: 'PATCH',
        path: '/api/v1/labels/Work',
        body: { name: null, color: 'personal' },
      }),
    );
    expect(screen.queryByRole('group', { name: 'Цвет метки' })).not.toBeInTheDocument();
  });

  it('renames a label in place and follows it when it is the open one', async () => {
    const user = userEvent.setup();
    const calls = mockApi([{ name: 'Work', color: null }]);
    const router = renderLabels('Work');

    await user.click(await screen.findByRole('button', { name: 'Действия с меткой «Work»' }));
    await user.click(screen.getByRole('button', { name: 'Переименовать' }));
    const field = screen.getByRole('textbox', { name: 'Новое название метки «Work»' });
    expect(field).toHaveValue('Work');
    await user.clear(field);
    await user.type(field, 'Job{Enter}');

    await waitFor(() =>
      expect(calls).toContainEqual({
        method: 'PATCH',
        path: '/api/v1/labels/Work',
        body: { name: 'Job', color: null },
      }),
    );
    // the mock answers with the name "x": the address follows the name the server saved
    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/@label/x'));
  });

  it('does not rename when the name is left as it was', async () => {
    const user = userEvent.setup();
    const calls = mockApi([{ name: 'Work', color: null }]);
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Действия с меткой «Work»' }));
    await user.click(screen.getByRole('button', { name: 'Переименовать' }));
    await user.type(
      screen.getByRole('textbox', { name: 'Новое название метки «Work»' }),
      '{Enter}',
    );

    expect(calls).toEqual([]);
    expect(screen.getByRole('link', { name: 'Work' })).toBeInTheDocument();
  });

  it('asks before deleting, says the messages stay, and deletes on confirmation', async () => {
    const user = userEvent.setup();
    const calls = mockApi([{ name: 'Work', color: null }]);
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Действия с меткой «Work»' }));
    await user.click(screen.getByRole('button', { name: 'Удалить' }));
    const dialog = screen.getByRole('dialog', { name: 'Удалить метку «Work»?' });
    expect(dialog).toHaveTextContent('Сами письма останутся на месте');
    expect(calls).toEqual([]);

    await user.click(within(dialog).getByRole('button', { name: 'Удалить метку' }));

    await waitFor(() =>
      expect(calls).toContainEqual({
        method: 'DELETE',
        path: '/api/v1/labels/Work',
        body: undefined,
      }),
    );
  });

  it('keeps the label when the deletion is cancelled', async () => {
    const user = userEvent.setup();
    const calls = mockApi([{ name: 'Work', color: null }]);
    renderLabels();

    await user.click(await screen.findByRole('button', { name: 'Действия с меткой «Work»' }));
    await user.click(screen.getByRole('button', { name: 'Удалить' }));
    await user.click(screen.getByRole('button', { name: 'Отмена' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(calls).toEqual([]);
  });

  it('goes to the inbox when the open label is deleted', async () => {
    const user = userEvent.setup();
    mockApi([{ name: 'Work', color: null }]);
    const router = renderLabels('Work');
    await router.navigate('/mail/@label/Work');

    await user.click(await screen.findByRole('button', { name: 'Действия с меткой «Work»' }));
    await user.click(screen.getByRole('button', { name: 'Удалить' }));
    await user.click(screen.getByRole('button', { name: 'Удалить метку' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
  });

  it('offers to try again when the labels cannot be loaded', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(() =>
        Promise.resolve(json({ code: 'mail_unavailable', title: 'x' }, { status: 503 })),
      ),
    );
    renderLabels();

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
    expect(screen.getByRole('button', { name: 'Повторить' })).toBeInTheDocument();
  });
});
