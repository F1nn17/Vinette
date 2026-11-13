import { QueryClient, type InfiniteData } from '@tanstack/react-query';
import { FOLDERS_KEY, type Folder } from '@/features/mailbox/api';
import { applyMailEvent } from './apply-event';
import type { MailEvent } from './events';

const item = (n: number, extra: Record<string, unknown> = {}) => ({
  id: `SU5CT1g.1.${n}`,
  from: null,
  to: [],
  subject: `S${n}`,
  date: '2026-11-13T10:00:00Z',
  seen: false,
  flagged: false,
  answered: false,
  draft: false,
  hasAttachments: false,
  labels: [] as string[],
  size: 1,
  preview: '',
  ...extra,
});

type Item = ReturnType<typeof item>;
type Page = { items: Item[]; next: null; total: number };

const list = (...items: Item[]): InfiniteData<Page> => ({
  pages: [{ items, next: null, total: items.length }],
  pageParams: [null],
});

const folder = (path: string, total: number, unread: number) =>
  ({
    path,
    name: path,
    parentPath: null,
    role: path === 'INBOX' ? 'inbox' : 'other',
    canContainMessages: true,
    total,
    unread,
    subscribed: true,
  }) as Folder;

function setup() {
  const client = new QueryClient();
  client.setQueryData(['messages', 'INBOX', 'all', true], list(item(1), item(2, { seen: true })));
  client.setQueryData(['messages', 'INBOX', 'unread', true], list(item(1)));
  client.setQueryData(['messages', 'INBOX', 'flagged', true], list());
  client.setQueryData(['messages', 'Work', 'all', true], list(item(9)));
  client.setQueryData(['message', 'SU5CT1g.1.1'], { ...item(1), body: 'x' });
  client.setQueryData(FOLDERS_KEY, [folder('INBOX', 2, 1), folder('Work', 1, 1)]);
  return client;
}

const rows = (client: QueryClient, key: readonly unknown[]) =>
  client.getQueryData<InfiniteData<Page>>(key)?.pages.flatMap((p) => p.items) ?? [];
const ids = (client: QueryClient, key: readonly unknown[]) => rows(client, key).map((r) => r.id);
const total = (client: QueryClient, key: readonly unknown[]) =>
  client.getQueryData<InfiniteData<Page>>(key)?.pages[0]?.total;
const isStale = (client: QueryClient, key: readonly unknown[]) =>
  client.getQueryState(key)?.isInvalidated;

const arrived = (message: Item): MailEvent =>
  ({ type: 'arrived', folder: 'INBOX', message }) as unknown as MailEvent;

const ALL = ['messages', 'INBOX', 'all', true] as const;
const UNREAD = ['messages', 'INBOX', 'unread', true] as const;
const FLAGGED = ['messages', 'INBOX', 'flagged', true] as const;
const WORK = ['messages', 'Work', 'all', true] as const;

describe('applyMailEvent: arrived', () => {
  it('puts the message at the top of the lists of its folder that would show it', () => {
    const client = setup();

    applyMailEvent(client, arrived(item(3)));

    expect(ids(client, ALL)).toEqual(['SU5CT1g.1.3', 'SU5CT1g.1.1', 'SU5CT1g.1.2']);
    expect(ids(client, UNREAD)).toEqual(['SU5CT1g.1.3', 'SU5CT1g.1.1']);
    expect(total(client, ALL)).toBe(3);
  });

  it('leaves out the lists it does not belong in: a read message in "unread", an unflagged one in "flagged"', () => {
    const client = setup();

    applyMailEvent(client, arrived(item(3, { seen: true })));

    expect(ids(client, UNREAD)).toEqual(['SU5CT1g.1.1']);
    expect(ids(client, FLAGGED)).toEqual([]);
    expect(ids(client, ALL)[0]).toBe('SU5CT1g.1.3');
  });

  it('puts a flagged message into "flagged"', () => {
    const client = setup();

    applyMailEvent(client, arrived(item(3, { flagged: true })));

    expect(ids(client, FLAGGED)).toEqual(['SU5CT1g.1.3']);
  });

  it('does not touch the lists of other folders', () => {
    const client = setup();

    applyMailEvent(client, arrived(item(3)));

    expect(ids(client, WORK)).toEqual(['SU5CT1g.1.9']);
  });

  it('does not put a message in twice (the screen may have it already)', () => {
    const client = setup();

    applyMailEvent(client, arrived(item(3)));
    applyMailEvent(client, arrived(item(3)));
    applyMailEvent(client, arrived(item(1)));

    expect(ids(client, ALL)).toEqual(['SU5CT1g.1.3', 'SU5CT1g.1.1', 'SU5CT1g.1.2']);
    expect(total(client, ALL)).toBe(3);
  });

  it('does nothing when no list has been loaded', () => {
    const client = new QueryClient();

    applyMailEvent(client, arrived(item(3)));

    expect(client.getQueryCache().getAll()).toHaveLength(0);
  });
});

describe('applyMailEvent: marks', () => {
  const marks = (extra: Record<string, unknown> = {}): MailEvent =>
    ({
      type: 'marks',
      folder: 'INBOX',
      id: 'SU5CT1g.1.1',
      seen: true,
      flagged: true,
      answered: false,
      labels: ['Work'],
      ...extra,
    }) as unknown as MailEvent;

  it('sets the marks on the rows of every list and on the open message', () => {
    const client = setup();

    applyMailEvent(client, marks());

    expect(rows(client, ALL)[0]).toMatchObject({ seen: true, flagged: true, labels: ['Work'] });
    expect(rows(client, UNREAD)[0]).toMatchObject({ seen: true });
    expect(client.getQueryData(['message', 'SU5CT1g.1.1'])).toMatchObject({
      seen: true,
      flagged: true,
      labels: ['Work'],
      body: 'x',
    });
    expect(rows(client, WORK)[0]).toMatchObject({ seen: false });
  });

  it('reads the "unread" and "flagged" lists again, as the message may belong to others now, and no other list', () => {
    const client = setup();

    applyMailEvent(client, marks());

    expect(isStale(client, UNREAD)).toBe(true);
    expect(isStale(client, FLAGGED)).toBe(true);
    expect(isStale(client, ALL)).toBeFalsy();
    expect(isStale(client, WORK)).toBeFalsy();
  });

  it('is harmless for a message the screen does not have', () => {
    const client = setup();

    applyMailEvent(client, marks({ id: 'SU5CT1g.1.99' }));

    expect(ids(client, ALL)).toEqual(['SU5CT1g.1.1', 'SU5CT1g.1.2']);
  });
});

describe('applyMailEvent: counters', () => {
  it('sets the counters of the folder and leaves the others', () => {
    const client = setup();

    applyMailEvent(client, { type: 'counters', folder: 'INBOX', total: 7, unread: 3 } as MailEvent);

    const folders = client.getQueryData<Folder[]>(FOLDERS_KEY) ?? [];
    expect(folders.map((f) => [f.path, Number(f.total), Number(f.unread)])).toEqual([
      ['INBOX', 7, 3],
      ['Work', 1, 1],
    ]);
  });

  it('does nothing before the folders are known', () => {
    const client = new QueryClient();

    applyMailEvent(client, { type: 'counters', folder: 'INBOX', total: 7, unread: 3 } as MailEvent);

    expect(client.getQueryData(FOLDERS_KEY)).toBeUndefined();
  });
});

describe.each([
  ['removed', { type: 'removed', folder: 'INBOX', count: 2 }],
  ['stale', { type: 'stale', folder: 'INBOX' }],
])('applyMailEvent: %s', (_name, event) => {
  it('makes the lists, the open message and the folders be read again', () => {
    const client = setup();

    applyMailEvent(client, event as MailEvent);

    expect(isStale(client, ALL)).toBe(true);
    expect(isStale(client, WORK)).toBe(true);
    expect(isStale(client, ['message', 'SU5CT1g.1.1'])).toBe(true);
    expect(isStale(client, FOLDERS_KEY)).toBe(true);
  });
});
