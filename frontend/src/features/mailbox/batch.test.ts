import { QueryClient, type InfiniteData } from '@tanstack/react-query';
import { applyBatch, folderOfId, restore, takeSnapshot } from './batch';
import { FOLDERS_KEY, type Folder } from './api';

// The id of a message of "INBOX": base64url("INBOX") = "SU5CT1g".
const id = (n: number, folder = 'SU5CT1g') => `${folder}.1.${n}`;

const folder = (path: string, role: string, total: number, unread: number) =>
  ({
    path,
    name: path,
    parentPath: null,
    role,
    canContainMessages: true,
    total,
    unread,
    subscribed: true,
  }) as Folder;

const item = (n: number, seen: boolean, flagged = false) => ({
  id: id(n),
  from: null,
  to: [],
  subject: `S${n}`,
  date: '2026-11-12T10:00:00Z',
  seen,
  flagged,
  answered: false,
  draft: false,
  hasAttachments: false,
  labels: [],
  size: 1,
  preview: '',
});

const detail = (n: number, seen: boolean) => ({ id: id(n), seen, flagged: false });

function setup() {
  const client = new QueryClient();
  const list: InfiniteData<{ items: ReturnType<typeof item>[]; next: null }> = {
    pages: [{ items: [item(1, false), item(2, true), item(3, false), item(4, true)], next: null }],
    pageParams: [null],
  };
  client.setQueryData(['messages', 'INBOX', 'all', true], list);
  client.setQueryData(['messages', 'INBOX', 'unread', true], {
    pages: [{ items: [item(1, false), item(3, false)], next: null }],
    pageParams: [null],
  });
  client.setQueryData(['message', id(1)], detail(1, false));
  client.setQueryData(FOLDERS_KEY, [
    folder('INBOX', 'inbox', 10, 4),
    folder('Archive', 'archive', 5, 0),
    folder('Junk', 'junk', 2, 1),
    folder('Trash', 'trash', 3, 0),
  ]);
  return client;
}

const rows = (client: QueryClient, key: readonly unknown[]) =>
  client
    .getQueryData<InfiniteData<{ items: { id: string; seen: boolean; flagged: boolean }[] }>>(key)
    ?.pages.flatMap((p) => p.items) ?? [];
const folders = (client: QueryClient) => client.getQueryData<Folder[]>(FOLDERS_KEY) ?? [];
const counters = (client: QueryClient, path: string) => {
  const f = folders(client).find((x) => x.path === path);
  return [Number(f?.total), Number(f?.unread)];
};

describe('folderOfId', () => {
  it('reads the folder out of the id', () => {
    expect(folderOfId('SU5CT1g.1.7')).toBe('INBOX');
    // "Работа/Отчёты" in base64url
    expect(folderOfId('0KDQsNCx0L7RgtCwL9Ce0YLRh9GR0YLRiw.1.7')).toBe('Работа/Отчёты');
  });

  it('gives null for something that is not an id', () => {
    expect(folderOfId('')).toBeNull();
    expect(folderOfId('!!!.1.2')).toBeNull();
  });
});

describe('applyBatch: a message moves', () => {
  it.each(['archive', 'spam', 'trash'] as const)(
    '"%s" takes the messages out of every cached list',
    (action) => {
      const client = setup();

      applyBatch(client, [id(1), id(2)], action);

      expect(rows(client, ['messages', 'INBOX', 'all', true]).map((m) => m.id)).toEqual([
        id(3),
        id(4),
      ]);
      expect(rows(client, ['messages', 'INBOX', 'unread', true]).map((m) => m.id)).toEqual([id(3)]);
    },
  );

  it('moves the counters: the folder it leaves loses the messages and the unread ones, the one it arrives in gains them', () => {
    const client = setup();

    applyBatch(client, [id(1), id(2)], 'archive'); // one of them was unread

    expect(counters(client, 'INBOX')).toEqual([8, 3]);
    expect(counters(client, 'Archive')).toEqual([7, 1]);
    expect(counters(client, 'Trash')).toEqual([3, 0]); // other folders are not touched
  });

  it('goes to the folder of the kind the action names', () => {
    const client = setup();

    applyBatch(client, [id(3)], 'spam');
    applyBatch(client, [id(4)], 'trash');

    expect(counters(client, 'Junk')).toEqual([3, 2]);
    expect(counters(client, 'Trash')).toEqual([4, 0]);
  });

  it('never makes a counter negative', () => {
    const client = setup();
    client.setQueryData(FOLDERS_KEY, [
      folder('INBOX', 'inbox', 1, 0),
      folder('Archive', 'archive', 0, 0),
    ]);

    applyBatch(client, [id(1), id(2), id(3)], 'archive');

    expect(counters(client, 'INBOX')).toEqual([0, 0]);
  });

  it('leaves the open message alone (the screen leaves it)', () => {
    const client = setup();

    applyBatch(client, [id(1)], 'trash');

    expect(client.getQueryData(['message', id(1)])).toEqual(detail(1, false));
  });
});

describe('applyBatch: marks', () => {
  it('"read" marks rows and the open message as read and lowers the unread count by the messages that were unread', () => {
    const client = setup();

    applyBatch(client, [id(1), id(2), id(3)], 'read'); // 1 and 3 were unread, 2 was read

    expect(rows(client, ['messages', 'INBOX', 'all', true]).map((m) => m.seen)).toEqual([
      true,
      true,
      true,
      true,
    ]);
    expect(client.getQueryData(['message', id(1)])).toEqual(detail(1, true));
    expect(counters(client, 'INBOX')).toEqual([10, 2]);
  });

  it('"unread" raises the unread count by the messages that were read', () => {
    const client = setup();

    applyBatch(client, [id(1), id(2), id(4)], 'unread'); // 2 and 4 were read

    expect(rows(client, ['messages', 'INBOX', 'all', true]).map((m) => m.seen)).toEqual([
      false,
      false,
      false,
      false,
    ]);
    expect(counters(client, 'INBOX')).toEqual([10, 6]);
  });

  it('"flag" and "unflag" change the flag and nothing else', () => {
    const client = setup();

    applyBatch(client, [id(2)], 'flag');
    expect(rows(client, ['messages', 'INBOX', 'all', true]).map((m) => m.flagged)).toEqual([
      false,
      true,
      false,
      false,
    ]);
    applyBatch(client, [id(2)], 'unflag');
    expect(rows(client, ['messages', 'INBOX', 'all', true]).map((m) => m.flagged)).toEqual([
      false,
      false,
      false,
      false,
    ]);
    expect(counters(client, 'INBOX')).toEqual([10, 4]);
  });

  it('keeps every message in the list (nothing moves)', () => {
    const client = setup();

    applyBatch(client, [id(1)], 'read');

    expect(rows(client, ['messages', 'INBOX', 'all', true])).toHaveLength(4);
  });
});

describe('restore', () => {
  it.each(['archive', 'read', 'flag'] as const)(
    'puts back exactly what was there before "%s"',
    (action) => {
      const client = setup();
      const before = takeSnapshot(client);
      const lists = structuredClone(before.lists);
      const folderList = structuredClone(before.folders);

      applyBatch(client, [id(1), id(3)], action);
      restore(client, before);

      expect(client.getQueriesData({ queryKey: ['messages'] })).toEqual(lists);
      expect(folders(client)).toEqual(folderList);
      expect(client.getQueryData(['message', id(1)])).toEqual(detail(1, false));
    },
  );
});

describe('applyBatch: deleting for good', () => {
  it('takes the messages out of every list and out of the count of their folder, and puts them nowhere', () => {
    const client = setup();

    applyBatch(client, [id(1), id(2)], 'delete');

    expect(rows(client, ['messages', 'INBOX', 'all', true]).map((r) => r.id)).toEqual([
      id(3),
      id(4),
    ]);
    expect(rows(client, ['messages', 'INBOX', 'unread', true]).map((r) => r.id)).toEqual([id(3)]);
    expect(counters(client, 'INBOX')).toEqual([8, 3]);
    // no folder gets them: they are gone
    expect(counters(client, 'Trash')).toEqual([3, 0]);
    expect(counters(client, 'Archive')).toEqual([5, 0]);
    expect(counters(client, 'Junk')).toEqual([2, 1]);
  });
});
