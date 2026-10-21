import type { components, paths } from './schema';

type HealthResponse =
  paths['/api/v1/health']['get']['responses']['200']['content']['application/json'];
type CsrfResponse = components['schemas']['CsrfResponse'];

const CSRF_HEADER = 'X-CSRF-Token';
const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** An error answer of the API (problem+json, RFC 9457). `code` is the machine-readable code from docs/api-errors.md. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string | undefined,
    readonly correlationId: string | undefined,
    message: string,
    /** Seconds until a retry makes sense (Retry-After of a 429), when the server said so. */
    readonly retryAfterSeconds?: number,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

type UnauthorizedHandler = () => void;
let unauthorizedHandler: UnauthorizedHandler | null = null;

/**
 * Registers what happens when the server says the session is gone (401 on any call except sign-in itself, where
 * 401 means a wrong password). Returns a function that unregisters the handler.
 */
export function onUnauthorized(handler: UnauthorizedHandler): () => void {
  unauthorizedHandler = handler;
  return () => {
    if (unauthorizedHandler === handler) unauthorizedHandler = null;
  };
}

let csrfToken: Promise<string> | null = null;

/** Forgets the anti-forgery token: it is bound to the signed-in user, so it changes on sign-in and sign-out. */
export function resetCsrfToken(): void {
  csrfToken = null;
}

function getCsrfToken(): Promise<string> {
  csrfToken ??= fetch('/api/v1/csrf', { credentials: 'same-origin', cache: 'no-store' })
    .then(async (response) => {
      if (!response.ok) throw await toApiError(response);
      return ((await response.json()) as CsrfResponse).token;
    })
    .catch((error: unknown) => {
      csrfToken = null; // do not cache a failure
      throw error;
    });
  return csrfToken;
}

async function toApiError(response: Response): Promise<ApiError> {
  let code: string | undefined;
  let title = `Request failed: ${response.status}`;
  try {
    const problem = (await response.json()) as { code?: string; title?: string };
    code = problem.code;
    title = problem.title ?? title;
  } catch {
    // not a problem+json body
  }
  const retryAfter = Number(response.headers.get('Retry-After'));
  return new ApiError(
    response.status,
    code,
    response.headers.get('X-Correlation-Id') ?? undefined,
    title,
    Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined,
  );
}

interface RequestOptions {
  body?: unknown;
  signal?: AbortSignal;
}

/**
 * Calls the API. State-changing requests carry the anti-forgery token automatically: it is fetched on first use and
 * kept in memory; if the server says it is stale (`csrf_failed`) a fresh one is fetched and the request is retried once.
 */
export async function apiRequest<T>(
  method: string,
  path: string,
  { body, signal }: RequestOptions = {},
): Promise<T> {
  const changesState = !SAFE_METHODS.has(method.toUpperCase());

  const send = async (): Promise<Response> => {
    const headers: Record<string, string> = {};
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (changesState) headers[CSRF_HEADER] = await getCsrfToken();
    return fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: 'same-origin',
      signal,
    });
  };

  let response = await send();
  if (changesState && response.status === 403) {
    const error = await toApiError(response.clone());
    if (error.code === 'csrf_failed') {
      resetCsrfToken();
      response = await send();
    }
  }

  if (response.status === 401 && path !== '/api/v1/auth/login') unauthorizedHandler?.();
  if (!response.ok) throw await toApiError(response);
  if (changesState && path.startsWith('/api/v1/auth/')) resetCsrfToken(); // sign-in/out changes the user
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const apiGet = <T>(path: string, signal?: AbortSignal) =>
  apiRequest<T>('GET', path, { signal });

export const apiPost = <T>(path: string, body?: unknown, signal?: AbortSignal) =>
  apiRequest<T>('POST', path, { body, signal });

export const getHealth = (signal?: AbortSignal) => apiGet<HealthResponse>('/api/v1/health', signal);
