import { act, renderHook } from '@testing-library/react';
import { resetCsrfToken } from '@/shared/api/client';
import { EMPTY_COMPOSE, type ComposeValues } from './compose-values';
import { AUTOSAVE_DELAY_MS, buildDraftContent, useDraftAutosave, type DraftContent } from './draft';
import { EMPTY_BODY } from './editor-text';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const withTo = (address: string): ComposeValues => ({
  ...EMPTY_COMPOSE,
  to: [{ name: '', address }],
});

describe('buildDraftContent', () => {
  it('is nothing while the message is empty', () => {
    expect(buildDraftContent(EMPTY_COMPOSE, EMPTY_BODY, [])).toBeNull();
    expect(buildDraftContent({ ...EMPTY_COMPOSE, subject: '  ' }, EMPTY_BODY, [])).toBeNull();
  });

  it('is a draft as soon as there is a recipient, a subject, text or a file', () => {
    expect(buildDraftContent(withTo('a@x.test'), EMPTY_BODY, [])).not.toBeNull();
    expect(buildDraftContent({ ...EMPTY_COMPOSE, subject: 'Hi' }, EMPTY_BODY, [])).not.toBeNull();
    expect(
      buildDraftContent(EMPTY_COMPOSE, { plain: true, html: '', text: 'x' }, []),
    ).not.toBeNull();
    expect(buildDraftContent(EMPTY_COMPOSE, EMPTY_BODY, ['f1'])).not.toBeNull();
  });

  it('leaves out a recipient that is not an address, which the server would refuse the whole draft for', () => {
    const values: ComposeValues = {
      ...EMPTY_COMPOSE,
      to: [
        { name: 'Anna', address: 'a@x.test' },
        { name: '', address: 'anna' },
      ],
    };

    expect(buildDraftContent(values, EMPTY_BODY, [])?.to).toEqual([
      { name: 'Anna', address: 'a@x.test' },
    ]);
  });

  it('sends a formatted body as html and a plain one as text', () => {
    const rich = buildDraftContent(EMPTY_COMPOSE, { plain: false, html: '<p>Hi</p>', text: 'Hi' }, [
      'f1',
    ]);
    const plain = buildDraftContent(EMPTY_COMPOSE, { plain: true, html: '', text: 'Hi' }, []);

    expect([rich?.html, rich?.text, rich?.attachmentIds]).toEqual(['<p>Hi</p>', null, ['f1']]);
    expect([plain?.html, plain?.text]).toEqual([null, 'Hi']);
  });
});

describe('useDraftAutosave', () => {
  let saves: { body: Record<string, unknown>; keepalive: boolean | undefined }[];
  let release: (() => void) | null;
  let hold: boolean;
  let failing: boolean;
  let serial: number;

  beforeEach(() => {
    vi.useFakeTimers();
    resetCsrfToken();
    saves = [];
    release = null;
    hold = false;
    failing = false;
    serial = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string, init?: RequestInit) => {
        if (input === '/api/v1/csrf') return json({ token: 't' });
        saves.push({
          body: JSON.parse(String(init?.body)) as Record<string, unknown>,
          keepalive: init?.keepalive,
        });
        if (hold) await new Promise<void>((resolve) => (release = resolve));
        if (failing) return json({ title: 'x' }, { status: 503 });
        serial += 1;
        return json({
          id: `id-${serial}`,
          messageId: 'm-1',
          savedAt: '2026-11-18T14:41:00Z',
          previousRemoved: true,
        });
      }),
    );
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  const draft = (subject: string): DraftContent =>
    buildDraftContent({ ...EMPTY_COMPOSE, subject }, EMPTY_BODY, [])!;

  it('saves once, a pause after the last change, not after every change', async () => {
    const { rerender, result } = renderHook(({ c }) => useDraftAutosave(c), {
      initialProps: { c: draft('H') },
    });

    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS - 100));
    rerender({ c: draft('He') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS - 100));
    rerender({ c: draft('Hel') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS - 100));
    expect(saves).toHaveLength(0);

    await act(() => vi.advanceTimersByTimeAsync(200));

    expect(saves).toHaveLength(1);
    expect(saves[0]!.body.subject).toBe('Hel');
    expect(result.current.status).toEqual({ state: 'saved', at: new Date('2026-11-18T14:41:00Z') });
  });

  it('saves nothing for an empty message, and nothing again when nothing changed', async () => {
    const { rerender } = renderHook(({ c }) => useDraftAutosave(c), {
      initialProps: { c: null as DraftContent | null },
    });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS * 2));
    expect(saves).toHaveLength(0);

    rerender({ c: draft('Hi') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));
    rerender({ c: draft('Hi') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS * 2));

    expect(saves).toHaveLength(1);
  });

  it('replaces its own previous version: each save names the one before it', async () => {
    const { rerender } = renderHook(({ c }) => useDraftAutosave(c), {
      initialProps: { c: draft('One') },
    });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));

    rerender({ c: draft('Two') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));

    expect(saves.map((s) => [s.body.previousId, s.body.messageId])).toEqual([
      [null, null],
      ['id-1', 'm-1'],
    ]);
  });

  it('never runs two saves at once: a change during a save is saved right after it, from its result', async () => {
    hold = true;
    const { rerender } = renderHook(({ c }) => useDraftAutosave(c), {
      initialProps: { c: draft('One') },
    });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));
    expect(saves).toHaveLength(1);

    rerender({ c: draft('Two') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));
    expect(saves).toHaveLength(1); // the first is still running

    hold = false;
    await act(async () => {
      release?.();
      await vi.advanceTimersByTimeAsync(10);
    });

    expect(saves.map((s) => [s.body.subject, s.body.previousId])).toEqual([
      ['One', null],
      ['Two', 'id-1'],
    ]);
  });

  it('says when a save failed and tries again at the next change', async () => {
    failing = true;
    const { rerender, result } = renderHook(({ c }) => useDraftAutosave(c), {
      initialProps: { c: draft('One') },
    });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));
    expect(result.current.status).toEqual({ state: 'failed' });

    failing = false;
    rerender({ c: draft('Two') });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));

    expect(result.current.status.state).toBe('saved');
    expect(saves.at(-1)!.body.subject).toBe('Two');
  });

  it('saves at once when the page is hidden, letting the request outlive the page', async () => {
    renderHook(() => useDraftAutosave(draft('Unsaved')));

    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await vi.advanceTimersByTimeAsync(10);
    });
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });

    expect(saves).toHaveLength(1);
    expect(saves[0]!.keepalive).toBe(true);
  });

  it('saves at once when the page is closed and when the screen is left', async () => {
    const closed = renderHook(() => useDraftAutosave(draft('A')));
    await act(async () => {
      window.dispatchEvent(new Event('pagehide'));
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(saves.map((s) => s.body.subject)).toEqual(['A']);
    closed.unmount();

    const left = renderHook(() => useDraftAutosave(draft('B')));
    await act(async () => {
      left.unmount();
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(saves.map((s) => s.body.subject)).toEqual(['A', 'B']);
  });

  it('asks before the page is closed only while something is not saved', async () => {
    renderHook(() => useDraftAutosave(draft('Hi')));
    const ask = () => {
      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };

    expect(ask()).toBe(true);

    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));
    expect(ask()).toBe(false);
  });

  it('does not ask for an empty message', () => {
    renderHook(() => useDraftAutosave(null));
    const event = new Event('beforeunload', { cancelable: true });

    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });

  it('stops saving when it is settled, waits for the save that is running and tells the id of the draft', async () => {
    hold = true;
    const { rerender, result } = renderHook(({ c }) => useDraftAutosave(c), {
      initialProps: { c: draft('One') },
    });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS + 10));
    expect(saves).toHaveLength(1); // running

    let settled: string | null | undefined;
    await act(async () => {
      void result.current.settle().then((id) => (settled = id));
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(settled).toBeUndefined(); // still waiting for the running save

    rerender({ c: draft('Two') });
    hold = false;
    await act(async () => {
      release?.();
      await vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS * 2);
    });

    expect(settled).toBe('id-1');
    expect(saves).toHaveLength(1); // the change made after settling is not saved
  });

  it('does not ask before the page is closed once it is settled (the message is out)', async () => {
    const { result } = renderHook(() => useDraftAutosave(draft('Hi')));
    await act(async () => {
      await result.current.settle();
    });
    const event = new Event('beforeunload', { cancelable: true });

    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });

  it('takes saving up again when resumed', async () => {
    const { result } = renderHook(() => useDraftAutosave(draft('Hi')));
    await act(async () => {
      await result.current.settle();
    });
    await act(() => vi.advanceTimersByTimeAsync(AUTOSAVE_DELAY_MS * 2));
    expect(saves).toHaveLength(0);

    await act(async () => {
      result.current.resume();
      await vi.advanceTimersByTimeAsync(10);
    });

    expect(saves).toHaveLength(1);
  });
});
