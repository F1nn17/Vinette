/**
 * A stand-in for XMLHttpRequest in tests (jsdom sends nothing real): records what was sent and lets the test report progress and answer.
 * Only what `apiUpload` uses is there.
 */
export class FakeXhr {
  static instances: FakeXhr[] = [];

  method = '';
  url = '';
  headers: Record<string, string> = {};
  body: FormData | null = null;
  aborted = false;
  status = 0;
  responseText = '';
  upload: {
    onprogress: ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null;
  } = {
    onprogress: null,
  };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;
  private responseHeaders = '';

  constructor() {
    FakeXhr.instances.push(this);
  }

  /** Replaces the real class for one test; returns the function that puts it back. */
  static install(): () => void {
    FakeXhr.instances = [];
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    return () => vi.unstubAllGlobals();
  }

  open(method: string, url: string) {
    this.method = method;
    this.url = url;
  }

  setRequestHeader(name: string, value: string) {
    this.headers[name] = value;
  }

  send(body: FormData) {
    this.body = body;
  }

  abort() {
    this.aborted = true;
    this.onabort?.();
  }

  getAllResponseHeaders() {
    return this.responseHeaders;
  }

  progress(loaded: number, total: number) {
    this.upload.onprogress?.({ lengthComputable: true, loaded, total });
  }

  answer(status: number, body: unknown, headers = 'content-type: application/json\r\n') {
    this.status = status;
    this.responseText = body === undefined ? '' : JSON.stringify(body);
    this.responseHeaders = headers;
    this.onload?.();
  }

  fail() {
    this.onerror?.();
  }
}
