import {
  useMutation,
  useQueryClient,
  type InfiniteData,
  type QueryClient,
} from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { describeApiError } from '@/shared/api/describe-error';
import { apiPost, type ApiError } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';
import { useToast } from '@/shared/toast';
import { FOLDERS_KEY, type Folder } from './api';

export type BatchAction =
  'archive' | 'spam' | 'trash' | 'read' | 'unread' | 'flag' | 'unflag' | 'delete'; // for good, from the trash
export type BatchResult = components['schemas']['MessageBatchResultDto'];

type ListPage = components['schemas']['MessageListResponse'];
type Detail = components['schemas']['MessageDetailDto'];

/** The kind of folder an action moves to; the others change marks and move nothing. */
const TARGET_ROLE: Partial<Record<BatchAction, string>> = {
  archive: 'archive',
  spam: 'junk',
  trash: 'trash',
};

/** What the caches held before the action, to put back if the server refuses. */
export interface Snapshot {
  lists: [readonly unknown[], InfiniteData<ListPage> | undefined][];
  details: [readonly unknown[], Detail | undefined][];
  folders: Folder[] | undefined;
}

/** The folder a message id belongs to: the id is `base64url(folder).uidValidity.uid`. */
export function folderOfId(id: string): string | null {
  const part = id.split('.')[0];
  if (!part) return null;
  try {
    const binary = atob(part.replace(/-/g, '+').replace(/_/g, '/'));
    return new TextDecoder().decode(Uint8Array.from(binary, (c) => c.charCodeAt(0)));
  } catch {
    return null;
  }
}

const num = (value: unknown) => Number(value) || 0;

/**
 * Makes the screen show the result of an action at once (VIN-075): a message that moves leaves every cached list, marks
 * change on the rows and in the open message, and the counters of the folders follow (the unread count of the folder it
 * leaves, and of the one it arrives in). The server's answer settles it afterwards; if it refuses, `restore` puts the old
 * state back.
 */
export function applyBatch(queryClient: QueryClient, ids: readonly string[], action: BatchAction) {
  const wanted = new Set(ids);
  const moves = action in TARGET_ROLE;
  const leaves = moves || action === 'delete'; // gone from the lists, and from the folder it was in
  const seen = action === 'read' ? true : action === 'unread' ? false : undefined;
  const flagged = action === 'flag' ? true : action === 'unflag' ? false : undefined;

  // What the lists show of these messages: how many were unread (before the change), for the counters.
  const known = new Map<string, boolean>();
  for (const [, data] of queryClient.getQueriesData<InfiniteData<ListPage>>({
    queryKey: ['messages'],
  })) {
    for (const item of data?.pages.flatMap((p) => p.items) ?? []) {
      if (wanted.has(item.id)) known.set(item.id, item.seen);
    }
  }
  const unreadBefore = [...known.values()].filter((isSeen) => !isSeen).length;

  queryClient.setQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] }, (data) =>
    data
      ? {
          ...data,
          pages: data.pages.map((page) => ({
            ...page,
            items: leaves
              ? page.items.filter((item) => !wanted.has(item.id))
              : page.items.map((item) =>
                  wanted.has(item.id)
                    ? {
                        ...item,
                        ...(seen === undefined ? {} : { seen }),
                        ...(flagged === undefined ? {} : { flagged }),
                      }
                    : item,
                ),
          })),
        }
      : data,
  );

  if (!leaves) {
    for (const id of wanted) {
      queryClient.setQueriesData<Detail>({ queryKey: ['message', id] }, (message) =>
        message
          ? {
              ...message,
              ...(seen === undefined ? {} : { seen }),
              ...(flagged === undefined ? {} : { flagged }),
            }
          : message,
      );
    }
  }

  const source = ids[0] === undefined ? null : folderOfId(ids[0]);
  const role = TARGET_ROLE[action];
  queryClient.setQueryData<Folder[]>(FOLDERS_KEY, (folders) =>
    folders?.map((folder) => {
      if (leaves && folder.path === source) {
        return {
          ...folder,
          total: Math.max(0, num(folder.total) - ids.length),
          unread: Math.max(0, num(folder.unread) - unreadBefore),
        };
      }
      if (moves && folder.role === role) {
        return {
          ...folder,
          total: num(folder.total) + ids.length,
          unread: num(folder.unread) + unreadBefore,
        };
      }
      if (seen !== undefined && folder.path === source) {
        // Only messages that change state count: reading an already read message changes nothing.
        const changed = [...known.values()].filter((isSeen) => isSeen !== seen).length;
        return { ...folder, unread: Math.max(0, num(folder.unread) + (seen ? -changed : changed)) };
      }
      return folder;
    }),
  );
}

export function takeSnapshot(queryClient: QueryClient): Snapshot {
  return {
    lists: queryClient.getQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] }),
    details: queryClient.getQueriesData<Detail>({ queryKey: ['message'] }),
    folders: queryClient.getQueryData<Folder[]>(FOLDERS_KEY),
  };
}

export function restore(queryClient: QueryClient, snapshot: Snapshot) {
  for (const [key, data] of snapshot.lists) queryClient.setQueryData(key, data);
  for (const [key, data] of snapshot.details) queryClient.setQueryData(key, data);
  queryClient.setQueryData(FOLDERS_KEY, snapshot.folders);
}

/**
 * One action on the selected messages (at most 100 of one folder, see `MAX_SELECTED`). The screen follows at once; when
 * the server refuses, the old state comes back and a toast says what happened in plain words (DESIGN.md section 12).
 * Actions are sent one after another. Afterwards the lists and counters are loaded again, so the screen ends up showing
 * what the server has.
 */
export function useMessageBatch() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const { t } = useTranslation();
  return useMutation<BatchResult, ApiError, { ids: string[]; action: BatchAction }, Snapshot>({
    mutationKey: ['message-batch'],
    scope: { id: 'message-batch' },
    mutationFn: ({ ids, action }) =>
      apiPost<BatchResult>('/api/v1/messages/batch', { ids, action, to: null }),
    onMutate: async ({ ids, action }) => {
      await Promise.all([
        queryClient.cancelQueries({ queryKey: ['messages'] }),
        queryClient.cancelQueries({ queryKey: ['message'] }),
        queryClient.cancelQueries({ queryKey: FOLDERS_KEY }),
      ]);
      const snapshot = takeSnapshot(queryClient);
      applyBatch(queryClient, ids, action);
      return snapshot;
    },
    onSuccess: (result, { ids, action }) => {
      if (action === 'delete') {
        toast.show({ message: t('toast.deleted', { count: result.changed }) });
        return;
      }
      if (!(action in TARGET_ROLE)) return;
      // Where the messages are now (from the answer) and where they came from (from their ids): putting them back is
      // one more move. A server that does not say the new ids (no UIDPLUS) leaves no way to find them: no button then.
      const back = folderOfId(ids[0] ?? '');
      const now = result.moved.map((m) => m.id);
      const undoable = back !== null && now.length > 0 && now.every((id): id is string => !!id);
      toast.show({
        message: t(`toast.moved.${action}`, { count: result.changed }),
        action: undoable
          ? {
              label: t('toast.undo'),
              onSelect: () => {
                apiPost<BatchResult>('/api/v1/messages/batch', {
                  ids: now,
                  action: 'move',
                  to: back,
                })
                  .catch(() => toast.show({ tone: 'error', message: t('toast.undoFailed') }))
                  .finally(() =>
                    Promise.all([
                      queryClient.invalidateQueries({ queryKey: ['messages'] }),
                      queryClient.invalidateQueries({ queryKey: FOLDERS_KEY }),
                    ]),
                  );
              },
            }
          : undefined,
      });
    },
    onError: (error, _variables, snapshot) => {
      if (snapshot) restore(queryClient, snapshot);
      toast.show({ tone: 'error', message: describeApiError(error, t) });
    },
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['messages'] }),
        queryClient.invalidateQueries({ queryKey: FOLDERS_KEY }),
        queryClient.invalidateQueries({ queryKey: ['message'] }),
      ]);
    },
  });
}
