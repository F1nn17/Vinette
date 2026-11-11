import { useQuery } from '@tanstack/react-query';
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
