import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router';
import { appRoutes } from '@/app/routes';
import { apiGet, resetCsrfToken } from '@/shared/api/client';
import { RedirectIfSignedIn, RequireAuth, SessionProvider, useSession } from '.';
import { ApiError } from '@/shared/api/client';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

interface Server {
  signedIn: boolean;
  calls: { method: string; url: string; body?: unknown }[];
  /** When set, /api/v1/me never answers (loading state). */
  hang?: boolean;
}

function mockServer(initial: Partial<Server> = {}): Server {
  const server: Server = { signedIn: false, calls: [], ...initial };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      server.calls.push({
        method,
        url,
        body: init?.body ? JSON.parse(String(init.body)) : undefined,
      });

      if (url === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (url === '/api/v1/me') {
        if (server.hang) return new Promise<Response>(() => undefined);
        return Promise.resolve(
          server.signedIn
            ? json({ address: 'alice@vinette.test' })
            : json({ code: 'unauthorized', title: 'Sign in required.' }, { status: 401 }),
        );
      }
      if (url === '/api/v1/auth/login') {
        const body = JSON.parse(String(init?.body)) as { password: string };
        if (body.password !== 'right') {
          return Promise.resolve(
            json({ code: 'invalid_credentials', title: 'No.' }, { status: 401 }),
          );
        }
        server.signedIn = true;
        return Promise.resolve(json({ address: 'alice@vinette.test' }));
      }
      if (url === '/api/v1/auth/logout') {
        server.signedIn = false;
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      if (url === '/api/v1/expired') {
        return Promise.resolve(
          json({ code: 'unauthorized', title: 'Sign in required.' }, { status: 401 }),
        );
      }
      return Promise.resolve(json({}, { status: 404 }));
    }),
  );
  return server;
}

function renderRoutes(routes: RouteObject[], entry: Parameters<typeof createMemoryRouter>[1]) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(routes, entry);
  render(
    <QueryClientProvider client={queryClient}>
      <SessionProvider>
        <RouterProvider router={router} />
      </SessionProvider>
    </QueryClientProvider>,
  );
  return { router, queryClient };
}

const renderApp = (path: string) => renderRoutes(appRoutes, { initialEntries: [path] });

/** A stand-in login screen (the real form arrives with VIN-026) that exposes the session actions. */
function TestLogin() {
  const { signIn } = useSession();
  return (
    <div>
      <h1>Login</h1>
      <button type="button" onClick={() => void signIn('alice@vinette.test', 'right', true)}>
        sign in
      </button>
      <button
        type="button"
        onClick={() =>
          signIn('alice@vinette.test', 'wrong', false).catch((e: unknown) => {
            document.title = e instanceof ApiError ? (e.code ?? '') : 'unexpected';
          })
        }
      >
        sign in wrong
      </button>
    </div>
  );
}

function TestMail() {
  const { address, signOut } = useSession();
  return (
    <div>
      <h1>Mail of {address}</h1>
      <button type="button" onClick={() => void signOut()}>
        sign out
      </button>
    </div>
  );
}

const testRoutes: RouteObject[] = [
  { element: <RedirectIfSignedIn />, children: [{ path: '/login', element: <TestLogin /> }] },
  { element: <RequireAuth />, children: [{ path: '/mail/:folder', element: <TestMail /> }] },
];

describe('authentication', () => {
  beforeEach(() => {
    resetCsrfToken();
    document.title = '';
  });
  afterEach(() => vi.unstubAllGlobals());

  it('sends a signed-out user from a protected page to the login screen and remembers where they wanted to go', async () => {
    mockServer({ signedIn: false });

    const { router } = renderApp('/mail/inbox?q=1');

    expect(await screen.findByRole('heading', { level: 1, name: /Вход/ })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/login');
    expect(router.state.location.state).toEqual({ from: '/mail/inbox?q=1' });
  });

  it('shows the protected page to a signed-in user', async () => {
    mockServer({ signedIn: true });

    renderApp('/mail/inbox');

    expect(await screen.findByRole('heading', { level: 1, name: 'Почта' })).toBeInTheDocument();
  });

  it('shows a loading state until the server has answered', () => {
    mockServer({ hang: true });

    renderApp('/mail/inbox');

    expect(screen.getByRole('status')).toHaveTextContent('Загрузка…');
  });

  it('does not keep a signed-in user on the login screen', async () => {
    mockServer({ signedIn: true });

    const { router } = renderRoutes(appRoutes, {
      initialEntries: [{ pathname: '/login', state: { from: '/compose' } }],
    });

    await waitFor(() => expect(router.state.location.pathname).toBe('/compose'));
  });

  it('ignores a "from" that points outside the application', async () => {
    mockServer({ signedIn: true });

    const { router } = renderRoutes(appRoutes, {
      initialEntries: [{ pathname: '/login', state: { from: 'https://evil.example/' } }],
    });

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
  });

  it.each(['//evil.example/', '/\\evil.example'])(
    'ignores the "from" %s that would leave the application',
    async (from) => {
      mockServer({ signedIn: true });

      const { router } = renderRoutes(appRoutes, {
        initialEntries: [{ pathname: '/login', state: { from } }],
      });

      await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    },
  );

  it('signs the user out everywhere in the app when any call answers 401', async () => {
    const server = mockServer({ signedIn: true });
    const { router, queryClient } = renderApp('/mail/inbox');
    await screen.findByRole('heading', { level: 1, name: 'Почта' });
    queryClient.setQueryData(['mail', 'inbox'], ['private message list']);

    server.signedIn = false;
    await act(async () => {
      await apiGet('/api/v1/expired').catch(() => undefined);
    });

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(queryClient.getQueryData(['mail', 'inbox'])).toBeUndefined(); // nothing of the previous user is kept
  });

  it('signs in: asks the server, learns who the user is and leaves the login screen', async () => {
    const server = mockServer({ signedIn: false });
    const { router } = renderRoutes(testRoutes, { initialEntries: ['/login'] });

    await userEvent.click(await screen.findByRole('button', { name: 'sign in' }));

    expect(
      await screen.findByRole('heading', { name: 'Mail of alice@vinette.test' }),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/mail/inbox');
    const login = server.calls.find((c) => c.url === '/api/v1/auth/login');
    expect(login?.body).toEqual({
      address: 'alice@vinette.test',
      password: 'right',
      rememberMe: true,
    });
  });

  it('keeps the user on the login screen after a wrong password and reports the reason', async () => {
    mockServer({ signedIn: false });
    const { router } = renderRoutes(testRoutes, { initialEntries: ['/login'] });

    await userEvent.click(await screen.findByRole('button', { name: 'sign in wrong' }));

    await waitFor(() => expect(document.title).toBe('invalid_credentials'));
    expect(router.state.location.pathname).toBe('/login');
  });

  it('signs out: tells the server, forgets the user and returns to the login screen', async () => {
    const server = mockServer({ signedIn: true });
    const { router, queryClient } = renderRoutes(testRoutes, { initialEntries: ['/mail/inbox'] });
    await screen.findByRole('heading', { name: 'Mail of alice@vinette.test' });
    queryClient.setQueryData(['mail', 'inbox'], ['private']);

    await userEvent.click(screen.getByRole('button', { name: 'sign out' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(server.calls.some((c) => c.url === '/api/v1/auth/logout' && c.method === 'POST')).toBe(
      true,
    );
    expect(queryClient.getQueryData(['mail', 'inbox'])).toBeUndefined();
  });
});
