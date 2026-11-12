import { useMutation, useQueryClient, type InfiniteData } from '@tanstack/react-query';
import { apiRequest, type ApiError } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';
import { messageKey, type MessageDetail } from './api';

export type MessageFlags = components['schemas']['MessageFlagsDto'];
/** What to change; a mark that is not mentioned stays as it is. */
export interface FlagChange {
  seen?: boolean;
  flagged?: boolean;
}

type ListPage = components['schemas']['MessageListResponse'];
type Snapshot = {
  message: MessageDetail | undefined;
  lists: [readonly unknown[], InfiniteData<ListPage> | undefined][];
};

/** Applies marks to the message in its own cache entry and to its row in every cached page of every list. */
function applyMarks(
  queryClient: ReturnType<typeof useQueryClient>,
  id: string,
  marks: Partial<Pick<MessageDetail, 'seen' | 'flagged' | 'answered' | 'draft'>>,
) {
  queryClient.setQueryData<MessageDetail>(messageKey(id), (message) =>
    message ? { ...message, ...marks } : message,
  );
  queryClient.setQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] }, (data) =>
    data
      ? {
          ...data,
          pages: data.pages.map((page) => ({
            ...page,
            items: page.items.map((item) => (item.id === id ? { ...item, ...marks } : item)),
          })),
        }
      : data,
  );
}

/**
 * Marks a message read or unread, flags or unflags it. The screen follows at once (the message and its row in the list),
 * the server's answer settles it, and if the server refuses the old state comes back. Changes of one message are sent one
 * after another. The counters of the folders are asked for again afterwards; the lists are not (a row must not jump out
 * from under the reader because it is no longer "unread").
 */
export function useSetFlags(messageId: string | undefined) {
  const queryClient = useQueryClient();
  const id = messageId ?? '';
  return useMutation<MessageFlags, ApiError, FlagChange, Snapshot>({
    mutationKey: ['message-flags', id],
    scope: { id: `message-flags:${id}` },
    mutationFn: (change) =>
      apiRequest<MessageFlags>('PATCH', `/api/v1/messages/${encodeURIComponent(id)}/flags`, {
        body: { seen: change.seen ?? null, flagged: change.flagged ?? null },
      }),
    onMutate: async (change) => {
      await queryClient.cancelQueries({ queryKey: ['messages'] });
      await queryClient.cancelQueries({ queryKey: messageKey(id) });
      const snapshot: Snapshot = {
        message: queryClient.getQueryData<MessageDetail>(messageKey(id)),
        lists: queryClient.getQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] }),
      };
      applyMarks(queryClient, id, change);
      return snapshot;
    },
    onError: (_error, _change, snapshot) => {
      if (!snapshot) return;
      queryClient.setQueryData(messageKey(id), snapshot.message);
      for (const [key, data] of snapshot.lists) queryClient.setQueryData(key, data);
    },
    onSuccess: (state) => applyMarks(queryClient, id, state),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['folders'] }),
  });
}
