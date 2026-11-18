import { ApiError, apiUpload, onUnauthorized, resetCsrfToken } from './client';
import { FakeXhr } from './fake-xhr';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const file = () => new File(['hello'], 'a.txt', { type: 'text/plain' });
const tick = () => new Promise((r) => setTimeout(r, 0));

describe('apiUpload', () => {
  let restore: () => void;
  let tokens: number;

  beforeEach(() => {
    resetCsrfToken();
    tokens = 0;
    restore = FakeXhr.install();
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(json({ token: `token-${++tokens}` }))),
    );
  });
  afterEach(() => restore());

  it('sends the file as a form field with the anti-forgery token and reports progress', async () => {
    const progress: number[] = [];
    const result = apiUpload<{ id: string }>('/api/v1/attachments', file(), {
      onProgress: (p) => progress.push(p),
    });
    await tick();
    const xhr = FakeXhr.instances[0]!;

    expect(xhr.method).toBe('POST');
    expect(xhr.url).toBe('/api/v1/attachments');
    expect(xhr.headers['X-CSRF-Token']).toBe('token-1');
    expect((xhr.body!.get('file') as File).name).toBe('a.txt');
    xhr.progress(50, 100);
    xhr.progress(100, 100);
    xhr.answer(201, { id: 'x' });

    await expect(result).resolves.toEqual({ id: 'x' });
    expect(progress).toEqual([0.5, 1]);
  });

  it('turns an error answer into an ApiError with its code', async () => {
    const result = apiUpload('/api/v1/attachments', file());
    await tick();

    FakeXhr.instances[0]!.answer(
      413,
      { code: 'attachment_too_large', title: 'Too large' },
      'content-type: application/problem+json\r\nx-correlation-id: abc\r\n',
    );

    const error = (await result.catch((e: unknown) => e)) as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect([error.status, error.code, error.correlationId]).toEqual([
      413,
      'attachment_too_large',
      'abc',
    ]);
  });

  it('fetches a fresh token and sends again once when the token is stale', async () => {
    const result = apiUpload('/api/v1/attachments', file());
    await tick();
    FakeXhr.instances[0]!.answer(403, { code: 'csrf_failed', title: 'Stale' });
    await tick();
    await tick();

    const second = FakeXhr.instances[1]!;
    expect(second.headers['X-CSRF-Token']).toBe('token-2');
    second.answer(201, { id: 'y' });
    await expect(result).resolves.toEqual({ id: 'y' });
  });

  it('rejects with an AbortError when it is aborted, and with a TypeError when the network fails', async () => {
    const controller = new AbortController();
    const aborted = apiUpload('/api/v1/attachments', file(), { signal: controller.signal });
    await tick();
    controller.abort();
    await expect(aborted).rejects.toMatchObject({ name: 'AbortError' });
    expect(FakeXhr.instances[0]!.aborted).toBe(true);

    const failed = apiUpload('/api/v1/attachments', file());
    await tick();
    FakeXhr.instances[1]!.fail();
    await expect(failed).rejects.toBeInstanceOf(TypeError);
  });

  it('tells the application when the session is gone', async () => {
    const handler = vi.fn();
    const off = onUnauthorized(handler);
    const result = apiUpload('/api/v1/attachments', file());
    await tick();

    FakeXhr.instances[0]!.answer(401, { code: 'unauthorized', title: 'No' });

    await result.catch(() => undefined);
    expect(handler).toHaveBeenCalledOnce();
    off();
  });
});
