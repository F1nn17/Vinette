import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { appRoutes } from '@/app/routes';
import { resetCsrfToken } from '@/shared/api/client';
import { SessionProvider as SessionProviderForTests } from './SessionProvider';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

interface Server {
  signedIn: boolean;
  loginResponse: () => Response;
  logins: unknown[];
}

function mockServer(loginResponse?: () => Response): Server {
  const server: Server = {
    signedIn: false,
    logins: [],
    loginResponse: loginResponse ?? (() => json({ address: 'alice@vinette.test' })),
  };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = String(input);
      if (url === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (url === '/api/v1/me') {
        return Promise.resolve(
          server.signedIn
            ? json({ address: 'alice@vinette.test' })
            : json({ code: 'unauthorized', title: 'x' }, { status: 401 }),
        );
      }
      if (url === '/api/v1/auth/login') {
        server.logins.push(JSON.parse(String(init?.body)));
        const response = server.loginResponse();
        if (response.ok) server.signedIn = true;
        return Promise.resolve(response);
      }
      return Promise.resolve(json({}, { status: 404 }));
    }),
  );
  return server;
}

function renderLogin() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(appRoutes, { initialEntries: ['/login'] });
  render(
    <QueryClientProvider client={queryClient}>
      <SessionProviderForTests>
        <RouterProvider router={router} />
      </SessionProviderForTests>
    </QueryClientProvider>,
  );
  return router;
}

const problem =
  (code: string, status: number, headers: Record<string, string> = {}) =>
  () =>
    json({ code, title: 'technical title' }, { status, headers });

async function fill(address: string, password: string) {
  await userEvent.type(await screen.findByLabelText('Адрес почты'), address);
  await userEvent.type(screen.getByLabelText('Пароль'), password);
}

describe('login screen', () => {
  beforeEach(() => {
    resetCsrfToken();
    document.getElementById('vinette-config')?.remove();
  });
  afterEach(() => vi.unstubAllGlobals());

  it('has the page heading, labelled fields, the remember option and the submit button', async () => {
    mockServer();

    renderLogin();

    expect(await screen.findByRole('heading', { level: 1, name: 'Вход' })).toBeInTheDocument();
    expect(screen.getByLabelText('Адрес почты')).toHaveAttribute('type', 'email');
    expect(screen.getByLabelText('Адрес почты')).toHaveAttribute('autocomplete', 'username');
    expect(screen.getByLabelText('Пароль')).toHaveAttribute('autocomplete', 'current-password');
    expect(
      screen.getByRole('checkbox', { name: 'Оставаться в системе 30 дней' }),
    ).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Войти' })).toHaveAttribute('type', 'submit');
    expect(screen.getByText('Пароль не сохраняется в браузере.')).toBeInTheDocument();
  });

  it('does not offer signing in with a work account when none is configured', async () => {
    mockServer();

    renderLogin();
    await screen.findByRole('heading', { level: 1 });

    expect(screen.queryByText(/рабочий аккаунт/i)).not.toBeInTheDocument();
  });

  it('shows the brand panel with the headline, the configured tagline and the name', async () => {
    const config = document.createElement('script');
    config.id = 'vinette-config';
    config.type = 'application/json';
    config.textContent = JSON.stringify({
      branding: { name: 'Acme Mail', tagline: 'Почта Acme для всех' },
    });
    document.head.appendChild(config);
    mockServer();

    renderLogin();

    await screen.findByRole('heading', { level: 1 });
    expect(screen.getByText(/Почта,\s*которая принадлежит вам/)).toBeInTheDocument();
    expect(screen.getByText('Почта Acme для всех')).toBeInTheDocument();
    expect(screen.getByText('© Acme Mail')).toBeInTheDocument();
  });

  it('shows and hides the password with a button that names its action', async () => {
    mockServer();
    renderLogin();
    const field = await screen.findByLabelText('Пароль');

    await userEvent.click(screen.getByRole('button', { name: 'Показать пароль' }));
    expect(field).toHaveAttribute('type', 'text');

    await userEvent.click(screen.getByRole('button', { name: 'Скрыть пароль' }));
    expect(field).toHaveAttribute('type', 'password');
  });

  it('asks for missing fields without calling the server', async () => {
    const server = mockServer();
    renderLogin();

    await userEvent.click(await screen.findByRole('button', { name: 'Войти' }));

    expect(screen.getByText('Введите адрес почты.')).toBeInTheDocument();
    expect(screen.getByText('Введите пароль.')).toBeInTheDocument();
    expect(screen.getByLabelText('Адрес почты')).toBeInvalid();
    expect(server.logins).toHaveLength(0);
  });

  it('signs in with Enter and leaves for the mailbox', async () => {
    const server = mockServer();
    const router = renderLogin();

    await fill('  alice@vinette.test ', 'right{enter}');

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    expect(server.logins).toEqual([
      { address: 'alice@vinette.test', password: 'right', rememberMe: false },
    ]);
  });

  it('sends the remember option', async () => {
    const server = mockServer();
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('checkbox', { name: 'Оставаться в системе 30 дней' }));
    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    await waitFor(() => expect(server.logins).toHaveLength(1));
    expect(server.logins[0]).toMatchObject({ rememberMe: true });
  });

  it('says so in plain words when the address or password is wrong and stays on the screen', async () => {
    mockServer(problem('invalid_credentials', 401));
    const router = renderLogin();
    await fill('alice@vinette.test', 'wrong');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Неверный адрес или пароль.');
    expect(router.state.location.pathname).toBe('/login');
    expect(screen.getByRole('button', { name: 'Войти' })).toBeEnabled();
  });

  it('says that mail is unavailable without technical details', async () => {
    mockServer(problem('mail_unavailable', 503));
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна.');
  });

  it('tells how long to wait after too many attempts, rounded up to whole minutes', async () => {
    mockServer(problem('too_many_requests', 429, { 'Retry-After': '90' }));
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('через 2 мин');
  });

  it('shows a general message for anything unexpected', async () => {
    mockServer(problem('internal_error', 500));
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Не удалось войти.');
  });

  it('blocks a second submit while the first one is in progress', async () => {
    let release: () => void = () => undefined;
    const server = mockServer(() => json({ address: 'alice@vinette.test' }));
    const original = server.loginResponse;
    server.loginResponse = original;
    // Hold the sign-in answer back to observe the busy state.
    const fetchMock = vi.mocked(fetch);
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation((input, init) =>
      String(input) === '/api/v1/auth/login'
        ? new Promise<Response>((resolve) => {
            release = () => resolve(json({ address: 'alice@vinette.test' }));
          })
        : base(input, init),
    );
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    const busy = await screen.findByRole('button', { name: 'Входим…' });
    expect(busy).toBeDisabled();
    expect(busy).toHaveAttribute('aria-busy', 'true');
    release();
  });

  it('says that the connection is missing when the application cannot be reached', async () => {
    mockServer();
    const fetchMock = vi.mocked(fetch);
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation((input, init) =>
      String(input) === '/api/v1/auth/login'
        ? Promise.reject(new TypeError('Failed to fetch'))
        : base(input, init),
    );
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Нет связи.');
    expect(screen.getByRole('button', { name: 'Войти' })).toBeEnabled();
  });

  it('asks to check the entered data when the server rejects its format', async () => {
    mockServer(problem('validation_failed', 400));
    renderLogin();
    await fill('alice@vinette.test', 'right');

    await userEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('введены полностью');
  });
});
