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
    /** The address a `recipient_rejected` answer names: the one the mail server refused. */
    readonly address?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** `code` is the error code of the 401 answer, e.g. `session_revoked` when the mail provider rejected the saved password. */
type UnauthorizedHandler = (code?: string) => void;
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

let mailOutdatedHandler: (() => void) | null = null;

/**
 * Registers what happens when the server says a folder was recreated (409 `uid_validity_changed`): message ids
 * the client holds for it now point at other messages, so everything loaded from the mailbox must be dropped.
 */
export function onMailOutdated(handler: () => void): () => void {
  mailOutdatedHandler = handler;
  return () => {
    if (mailOutdatedHandler === handler) mailOutdatedHandler = null;
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
  let address: string | undefined;
  let title = `Request failed: ${response.status}`;
  try {
    const problem = (await response.json()) as { code?: string; title?: string; address?: string };
    code = problem.code;
    address = typeof problem.address === 'string' ? problem.address : undefined;
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
    address,
  );
}

interface RequestOptions {
  body?: unknown;
  signal?: AbortSignal;
  /** Lets the request finish after the page is closed (small bodies only: browsers cap them at 64 KB). */
  keepalive?: boolean;
}

/**
 * Calls the API. State-changing requests carry the anti-forgery token automatically: it is fetched on first use and
 * kept in memory; if the server says it is stale (`csrf_failed`) a fresh one is fetched and the request is retried once.
 */
export async function apiRequest<T>(
  method: string,
  path: string,
  { body, signal, keepalive }: RequestOptions = {},
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
      keepalive,
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

  return settle<T>(response, path, changesState);
}

/** Turns the answer into the result, or into the `ApiError` it carries (and tells the handlers what the error means for the whole app). */
async function settle<T>(response: Response, path: string, changesState: boolean): Promise<T> {
  if (!response.ok) {
    const error = await toApiError(response);
    if (response.status === 401 && path !== '/api/v1/auth/login') unauthorizedHandler?.(error.code);
    if (response.status === 409 && error.code === 'uid_validity_changed') mailOutdatedHandler?.();
    throw error;
  }
  if (changesState && path.startsWith('/api/v1/auth/')) resetCsrfToken(); // sign-in/out changes the user
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

interface UploadOptions {
  /** Called with the share of the file sent so far, 0 to 1. */
  onProgress?: (fraction: number) => void;
  signal?: AbortSignal;
}

/** The headers of an XMLHttpRequest answer, as `Headers`. */
function headersOf(xhr: XMLHttpRequest): Headers {
  const headers = new Headers();
  for (const line of xhr.getAllResponseHeaders().trim().split(/\r?\n/)) {
    const colon = line.indexOf(':');
    if (colon > 0) headers.append(line.slice(0, colon).trim(), line.slice(colon + 1).trim());
  }
  return headers;
}

/**
 * Sends one file as `multipart/form-data` (field `file`) and reports how much has gone. `fetch` cannot report the progress of what it
 * sends, so this uses XMLHttpRequest; the anti-forgery token, the stale-token retry and the errors are the same as in `apiRequest`.
 * Aborting through `signal` rejects with an `AbortError`.
 */
export async function apiUpload<T>(
  path: string,
  file: File,
  { onProgress, signal }: UploadOptions = {},
): Promise<T> {
  const send = async (): Promise<Response> => {
    const token = await getCsrfToken();
    return new Promise<Response>((resolve, reject) => {
      if (signal?.aborted) {
        reject(new DOMException('Aborted', 'AbortError'));
        return;
      }
      const xhr = new XMLHttpRequest();
      const abort = () => xhr.abort();
      signal?.addEventListener('abort', abort, { once: true });
      const done = () => signal?.removeEventListener('abort', abort);
      xhr.open('POST', path);
      xhr.setRequestHeader(CSRF_HEADER, token);
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && e.total > 0) onProgress?.(e.loaded / e.total);
      };
      xhr.onload = () => {
        done();
        const empty = xhr.status === 204 || xhr.status === 205;
        resolve(
          new Response(empty ? null : xhr.responseText, {
            status: xhr.status,
            headers: headersOf(xhr),
          }),
        );
      };
      xhr.onerror = () => {
        done();
        reject(new TypeError('Network error'));
      };
      xhr.onabort = () => {
        done();
        reject(new DOMException('Aborted', 'AbortError'));
      };
      const form = new FormData();
      form.append('file', file, file.name);
      xhr.send(form);
    });
  };

  let response = await send();
  if (response.status === 403) {
    const error = await toApiError(response.clone());
    if (error.code === 'csrf_failed') {
      resetCsrfToken();
      response = await send();
    }
  }
  return settle<T>(response, path, true);
}

export const apiGet = <T>(path: string, signal?: AbortSignal) =>
  apiRequest<T>('GET', path, { signal });

export const apiPost = <T>(
  path: string,
  body?: unknown,
  signal?: AbortSignal,
  keepalive?: boolean,
) => apiRequest<T>('POST', path, { body, signal, keepalive });

export const getHealth = (signal?: AbortSignal) => apiGet<HealthResponse>('/api/v1/health', signal);
