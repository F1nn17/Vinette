import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { apiGet } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

export type Folder = components['schemas']['FolderDto'];
export type Quota = components['schemas']['QuotaResponse'];

export const FOLDERS_KEY = ['folders'] as const;
export const QUOTA_KEY = ['quota'] as const;

/** The folders of the mailbox in the order to show them, with their counters. */
export function useFolders() {
  return useQuery({
    queryKey: FOLDERS_KEY,
    queryFn: async ({ signal }) =>
      (await apiGet<components['schemas']['FoldersResponse']>('/api/v1/folders', signal)).folders,
  });
}

/** How much of the mailbox is used; `storage` is null if the mail provider does not report it. */
export function useQuota() {
  return useQuery({
    queryKey: QUOTA_KEY,
    queryFn: ({ signal }) => apiGet<Quota>('/api/v1/quota', signal),
  });
}

export type MessageItem = components['schemas']['MessageListItemDto'];
export type MessageListResponse = components['schemas']['MessageListResponse'];
export type ListFilter = 'all' | 'unread' | 'flagged';

export const PAGE_SIZE = 50;

export const messagesKey = (folderPath: string, filter: ListFilter) =>
  ['messages', folderPath, filter] as const;

/** The messages of a folder, page after page (cursor paging): the newest first, narrowed by the filter. */
export function useMessages(folderPath: string | undefined, filter: ListFilter) {
  return useInfiniteQuery({
    queryKey: messagesKey(folderPath ?? '', filter),
    enabled: folderPath !== undefined,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (filter !== 'all') query.set('filter', filter);
      if (pageParam) query.set('after', pageParam);
      return apiGet<MessageListResponse>(
        `/api/v1/folders/${encodeURIComponent(folderPath ?? '')}/messages?${query.toString()}`,
        signal,
      );
    },
    getNextPageParam: (last) => last.next,
  });
}
