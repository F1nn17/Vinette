import type { InfiniteData, QueryClient } from '@tanstack/react-query';
import type { components } from '@/shared/api/schema';
import { FOLDERS_KEY, type Folder } from '@/features/mailbox/api';
import { consumeRemoval } from './echo';
import type { MailEvent } from './events';

type ListPage = components['schemas']['MessageListResponse'];
type Detail = components['schemas']['MessageDetailDto'];

const num = (value: unknown) => Number(value) || 0;

/** The keys of the lists of one folder are `['messages', folder, filter, preview]`. */
const listsOf = (folder: string) => ({ queryKey: ['messages', folder] });

/** Keys of the mutations that act on messages: while one runs, its optimistic state is the truth the screen should keep. */
const OWN_ACTIONS = ['message-batch', 'message-labels', 'message-flags'];

function isOurOwnActionRunning(queryClient: QueryClient): boolean {
  return (
    queryClient.isMutating({
      predicate: (mutation) => OWN_ACTIONS.includes(String(mutation.options.mutationKey?.[0])),
    }) > 0
  );
}

const sameLabels = (a: readonly string[], b: readonly string[]) =>
  a.length === b.length && a.every((label, i) => label === b[i]);

/** Whether the screen shows this message (a row of a list or the open message) with other marks than the event says. */
function differsFromScreen(
  queryClient: QueryClient,
  event: Extract<MailEvent, { type: 'marks' }>,
): boolean {
  const differs = (shown: {
    seen: boolean;
    flagged: boolean;
    answered: boolean;
    labels: string[];
  }) =>
    shown.seen !== event.seen ||
    shown.flagged !== event.flagged ||
    shown.answered !== event.answered ||
    !sameLabels(shown.labels, event.labels);
  const rows = queryClient
    .getQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] })
    .flatMap(([, data]) => data?.pages.flatMap((page) => page.items) ?? [])
    .filter((item) => item.id === event.id);
  const details = queryClient
    .getQueriesData<Detail>({ queryKey: ['message', event.id] })
    .flatMap(([, message]) => (message ? [message] : []));
  return [...rows, ...details].some(differs);
}

/** Everything the screen holds of the mail is read again. */
function readAgain(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: ['messages'] });
  void queryClient.invalidateQueries({ queryKey: ['message'] });
  void queryClient.invalidateQueries({ queryKey: FOLDERS_KEY });
}

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
      // Only how many were removed is known, not which. What our own actions removed is already off the screen (VIN-087); for
      // the rest (another program, another device) the lists and the open message are read again.
      if (consumeRemoval(queryClient, event.folder, Number(event.count)) > 0)
        readAgain(queryClient);
      return;
    case 'stale':
      readAgain(queryClient);
      return;
    case 'marks': {
      const { id, seen, flagged, answered, labels } = event;
      // While our own action on messages is still running, its end reads everything again: the echo has nothing to add.
      if (isOurOwnActionRunning(queryClient)) return;
      // The marks the screen already shows (the echo of our own action, which was shown at once) change nothing: no redraw.
      if (!differsFromScreen(queryClient, event)) return;
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
