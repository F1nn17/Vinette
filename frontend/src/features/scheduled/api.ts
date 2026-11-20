import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiRequest } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

export type ScheduledItem = components['schemas']['ScheduledDto'];

export const SCHEDULED_KEY = ['scheduled'] as const;

/** Where a scheduled message is opened. */
export const scheduledPath = (id?: string) =>
  id === undefined ? '/mail/@scheduled' : `/mail/@scheduled/${encodeURIComponent(id)}`;

/** How often the list is read again while the application is open: a failure should be noticed, and the counter should be true. */
export const SCHEDULED_REFRESH_MS = 60_000;

/**
 * The messages put off with "Send later" (`GET /scheduled`): the ones that wait and the notes of those that did not go. Soonest first.
 * Read again every minute and when the window is shown again.
 */
export function useScheduled() {
  return useQuery({
    queryKey: SCHEDULED_KEY,
    queryFn: async ({ signal }) =>
      (await apiGet<components['schemas']['ScheduledListResponse']>('/api/v1/scheduled', signal))
        .items,
    refetchInterval: SCHEDULED_REFRESH_MS,
    refetchOnWindowFocus: true,
    staleTime: 30_000,
    retry: false,
  });
}

/** How many messages wait, and whether one did not go: what the sidebar shows. */
export function summaryOf(items: readonly ScheduledItem[] | undefined) {
  const list = items ?? [];
  return {
    waiting: list.filter((i) => i.state === 'pending').length,
    failed: list.filter((i) => i.state === 'failed').length,
  };
}

/**
 * Cancels a message (`DELETE /scheduled/{id}`): it goes back among the drafts and leaves the queue; for the note of a failure only the note goes.
 * The drafts and the counters of the folders are read again, as they changed.
 */
export function useCancelScheduled() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      apiRequest<void>('DELETE', `/api/v1/scheduled/${encodeURIComponent(id)}`),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: SCHEDULED_KEY }),
        queryClient.invalidateQueries({ queryKey: ['folders'] }),
        queryClient.invalidateQueries({ queryKey: ['messages'] }),
      ]);
    },
  });
}
