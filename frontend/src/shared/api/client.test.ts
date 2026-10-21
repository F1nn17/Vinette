import { ApiError, apiGet, apiPost, onUnauthorized, resetCsrfToken } from './client';

type FetchMock = ReturnType<typeof vi.fn<typeof fetch>>;

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });

function mockFetch(handler: (url: string, init?: RequestInit) => Response): FetchMock {
  const mock = vi.fn<typeof fetch>((input, init) => Promise.resolve(handler(String(input), init)));
  vi.stubGlobal('fetch', mock);
  return mock;
}

const callsTo = (mock: FetchMock, url: string) => mock.mock.calls.filter(([u]) => u === url);
const headerOf = (call: Parameters<typeof fetch>, name: string) =>
  (call[1]?.headers as Record<string, string> | undefined)?.[name];

describe('API client', () => {
  beforeEach(() => resetCsrfToken());
  afterEach(() => vi.unstubAllGlobals());

  it('does not ask for a token on reads', async () => {
    const mock = mockFetch(() => json({ status: 'ok' }));

    await apiGet('/api/v1/health');

    expect(callsTo(mock, '/api/v1/csrf')).toHaveLength(0);
  });

  it('adds the token to state-changing requests and fetches it only once', async () => {
    const mock = mockFetch((url) =>
      url === '/api/v1/csrf' ? json({ token: 'token-1' }) : json({ ok: true }),
    );

    await apiPost('/api/v1/mail/something', { a: 1 });
    await apiPost('/api/v1/mail/something', { a: 2 });

    expect(callsTo(mock, '/api/v1/csrf')).toHaveLength(1);
    const posts = callsTo(mock, '/api/v1/mail/something');
    expect(posts).toHaveLength(2);
    expect(headerOf(posts[0]!, 'X-CSRF-Token')).toBe('token-1');
    expect(posts[0]![1]?.body).toBe('{"a":1}');
    expect(posts[0]![1]?.credentials).toBe('same-origin');
  });

  it('fetches a fresh token and retries once when the server says the token is stale', async () => {
    let tokens = 0;
    let posts = 0;
    const mock = mockFetch((url) => {
      if (url === '/api/v1/csrf') return json({ token: `token-${++tokens}` });
      posts += 1;
      return posts === 1
        ? json({ code: 'csrf_failed', title: 'stale' }, { status: 403 })
        : json({});
    });

    await apiPost('/api/v1/mail/something');

    const sent = callsTo(mock, '/api/v1/mail/something');
    expect(sent).toHaveLength(2);
    expect(headerOf(sent[0]!, 'X-CSRF-Token')).toBe('token-1');
    expect(headerOf(sent[1]!, 'X-CSRF-Token')).toBe('token-2');
  });

  it('does not retry other 403 answers', async () => {
    const mock = mockFetch((url) =>
      url === '/api/v1/csrf'
        ? json({ token: 't' })
        : json({ code: 'origin_not_allowed', title: 'no' }, { status: 403 }),
    );

    await expect(apiPost('/api/v1/mail/something')).rejects.toMatchObject({
      status: 403,
      code: 'origin_not_allowed',
    });
    expect(callsTo(mock, '/api/v1/mail/something')).toHaveLength(1);
  });

  it('gives up after one retry', async () => {
    const mock = mockFetch((url) =>
      url === '/api/v1/csrf'
        ? json({ token: 't' })
        : json({ code: 'csrf_failed', title: 'stale' }, { status: 403 }),
    );

    await expect(apiPost('/api/v1/mail/something')).rejects.toBeInstanceOf(ApiError);
    expect(callsTo(mock, '/api/v1/mail/something')).toHaveLength(2);
  });

  it('refetches the token after sign-in or sign-out because the user changes', async () => {
    const mock = mockFetch((url) => (url === '/api/v1/csrf' ? json({ token: 't' }) : json({})));

    await apiPost('/api/v1/auth/login', { address: 'a', password: 'p' });
    await apiPost('/api/v1/mail/something');

    expect(callsTo(mock, '/api/v1/csrf')).toHaveLength(2);
  });

  it('turns problem+json answers into ApiError with code and correlation id', async () => {
    mockFetch(() =>
      json(
        { code: 'invalid_credentials', title: 'The address or password is incorrect.' },
        { status: 401, headers: { 'X-Correlation-Id': 'abc123' } },
      ),
    );

    const error = await apiGet('/api/v1/me').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({
      status: 401,
      code: 'invalid_credentials',
      correlationId: 'abc123',
      message: 'The address or password is incorrect.',
    });
  });

  it('copes with error answers that are not problem+json', async () => {
    mockFetch(() => new Response('Bad gateway', { status: 502 }));

    await expect(apiGet('/api/v1/me')).rejects.toMatchObject({ status: 502, code: undefined });
  });

  it('returns undefined for 204 answers', async () => {
    mockFetch((url) =>
      url === '/api/v1/csrf' ? json({ token: 't' }) : new Response(null, { status: 204 }),
    );

    await expect(apiPost('/api/v1/auth/logout')).resolves.toBeUndefined();
  });

  it('does not cache a failed token request', async () => {
    let attempts = 0;
    const mock = mockFetch((url) => {
      if (url === '/api/v1/csrf') {
        attempts += 1;
        return attempts === 1 ? new Response('', { status: 500 }) : json({ token: 't' });
      }
      return json({});
    });

    await expect(apiPost('/api/v1/mail/something')).rejects.toBeInstanceOf(ApiError);
    await apiPost('/api/v1/mail/something');

    expect(callsTo(mock, '/api/v1/csrf')).toHaveLength(2);
  });

  describe('unauthorized handler', () => {
    it('is called when the server answers 401', async () => {
      mockFetch(() => json({ code: 'unauthorized', title: 'Sign in required.' }, { status: 401 }));
      const handler = vi.fn();
      const off = onUnauthorized(handler);

      await expect(apiGet('/api/v1/me')).rejects.toMatchObject({ status: 401 });

      expect(handler).toHaveBeenCalledOnce();
      off();
    });

    it('is not called for a wrong password: there 401 is the answer, not a lost session', async () => {
      mockFetch((url) =>
        url === '/api/v1/csrf'
          ? json({ token: 't' })
          : json({ code: 'invalid_credentials', title: 'No.' }, { status: 401 }),
      );
      const handler = vi.fn();
      const off = onUnauthorized(handler);

      await expect(apiPost('/api/v1/auth/login', {})).rejects.toMatchObject({
        code: 'invalid_credentials',
      });

      expect(handler).not.toHaveBeenCalled();
      off();
    });

    it('is not called for other errors or after unregistering', async () => {
      mockFetch(() => json({ title: 'Broken' }, { status: 500 }));
      const handler = vi.fn();
      const off = onUnauthorized(handler);

      await apiGet('/api/v1/me').catch(() => undefined);
      off();
      mockFetch(() => json({}, { status: 401 }));
      await apiGet('/api/v1/me').catch(() => undefined);

      expect(handler).not.toHaveBeenCalled();
    });
  });
});
