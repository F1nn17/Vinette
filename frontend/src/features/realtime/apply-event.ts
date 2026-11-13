import type { InfiniteData, QueryClient } from '@tanstack/react-query';
import type { components } from '@/shared/api/schema';
import { FOLDERS_KEY, type Folder } from '@/features/mailbox/api';
import type { MailEvent } from './events';

type ListPage = components['schemas']['MessageListResponse'];
type Detail = components['schemas']['MessageDetailDto'];

const num = (value: unknown) => Number(value) || 0;

/** The keys of the lists of one folder are `['messages', folder, filter, preview]`. */
const listsOf = (folder: string) => ({ queryKey: ['messages', folder] });

/**
 * Makes the screen show what the server says happened in a folder, without anyone asking: a message that arrived is put at the
 * top of the lists of its folder that would show it, marks are set on the rows and on the open message, the counters of the
 * folder are set, and what cannot be described (messages removed, a folder gone stale) is read again. Everything is idempotent:
 * an event about something the screen already shows (an action of this very user) changes nothing.
 */
export function applyMailEvent(queryClient: QueryClient, event: MailEvent): void {
  switch (event.type) {
    case 'arrived': {
      const { message } = event;
      for (const [key, data] of queryClient.getQueriesData<InfiniteData<ListPage>>(
        listsOf(event.folder),
      )) {
        const filter = key[2];
        if (!data || data.pages.length === 0) continue;
        if (data.pages.some((page) => page.items.some((item) => item.id === message.id))) continue;
        if ((filter === 'unread' && message.seen) || (filter === 'flagged' && !message.flagged))
          continue;
        const [first, ...others] = data.pages;
        if (!first) continue;
        queryClient.setQueryData<InfiniteData<ListPage>>(key, {
          ...data,
          pages: [
            { ...first, items: [message, ...first.items], total: num(first.total) + 1 },
            ...others,
          ],
        });
      }
      return;
    }
    case 'removed':
    case 'stale':
      // Only how many were removed is known, not which: the lists and the open message are read again.
      void queryClient.invalidateQueries({ queryKey: ['messages'] });
      void queryClient.invalidateQueries({ queryKey: ['message'] });
      void queryClient.invalidateQueries({ queryKey: FOLDERS_KEY });
      return;
    case 'marks': {
      const { id, seen, flagged, answered, labels } = event;
      queryClient.setQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] }, (data) =>
        data
          ? {
              ...data,
              pages: data.pages.map((page) => ({
                ...page,
                items: page.items.map((item) =>
                  item.id === id ? { ...item, seen, flagged, answered, labels } : item,
                ),
              })),
            }
          : data,
      );
      queryClient.setQueriesData<Detail>({ queryKey: ['message', id] }, (message) =>
        message ? { ...message, seen, flagged, answered, labels } : message,
      );
      // A message that was read or flagged may belong to another filter now: those lists are read again.
      void queryClient.invalidateQueries({
        queryKey: ['messages'],
        predicate: (query) => query.queryKey[2] === 'unread' || query.queryKey[2] === 'flagged',
      });
      return;
    }
    case 'counters':
      queryClient.setQueryData<Folder[]>(FOLDERS_KEY, (folders) =>
        folders?.map((folder) =>
          folder.path === event.folder
            ? { ...folder, total: event.total, unread: event.unread }
            : folder,
        ),
      );
      return;
  }
}
