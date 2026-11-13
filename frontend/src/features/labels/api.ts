import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
  type InfiniteData,
} from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { apiGet, apiRequest, type ApiError } from '@/shared/api/client';
import { describeApiError } from '@/shared/api/describe-error';
import type { components } from '@/shared/api/schema';
import { useToast } from '@/shared/toast';
import { restore, takeSnapshot, type Snapshot } from '@/features/mailbox/batch';
import { SETTINGS_KEY } from '@/features/settings';

export type Label = components['schemas']['LabelDto'];
type ListPage = components['schemas']['MessageListResponse'];
type Detail = components['schemas']['MessageDetailDto'];

export const LABELS_KEY = ['labels'] as const;

/** At most this many labels are put on or taken off one message in one request (the server's limit). */
export const MAX_LABEL_CHANGES = 20;

const labelUrl = (name: string) => `/api/v1/labels/${encodeURIComponent(name)}`;

/** The labels of the mailbox, by name. */
export function useLabels() {
  return useQuery({
    queryKey: LABELS_KEY,
    queryFn: async ({ signal }) =>
      (await apiGet<components['schemas']['LabelsResponse']>('/api/v1/labels', signal)).labels,
  });
}

/** The newest messages with a label from all the folders: one page, there is no next one. */
export function useLabelMessages(
  label: string | undefined,
  preview: boolean,
  /** False holds the request back (the settings that decide how to ask are not known yet). */
  ready = true,
) {
  return useInfiniteQuery({
    // Starts with `messages`, so that the actions of the list (move, mark) reach it like any other list.
    queryKey: ['messages', '@label', label ?? '', preview] as const,
    enabled: label !== undefined && ready,
    initialPageParam: null as string | null,
    queryFn: ({ signal }) =>
      apiGet<ListPage>(
        `${labelUrl(label ?? '')}/messages${preview ? '' : '?preview=false'}`,
        signal,
      ),
    getNextPageParam: () => undefined,
  });
}

/** What changed under the labels: the lists, the messages and the colors of the settings are read again. */
function useRefreshAfterChange() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: LABELS_KEY }),
      queryClient.invalidateQueries({ queryKey: SETTINGS_KEY }),
      queryClient.invalidateQueries({ queryKey: ['messages'] }),
      queryClient.invalidateQueries({ queryKey: ['message'] }),
    ]);
}

/** Creates a label (a name the user typed; the colour is chosen by the name until the user picks one). */
export function useCreateLabel() {
  const refresh = useRefreshAfterChange();
  return useMutation<Label, ApiError, { name: string; color?: string }>({
    mutationFn: ({ name, color }) =>
      apiRequest<Label>('POST', '/api/v1/labels', { body: { name, color: color ?? null } }),
    onSettled: refresh,
  });
}

/** Renames a label (on every message that has it) and/or changes its colour. */
export function useUpdateLabel() {
  const refresh = useRefreshAfterChange();
  return useMutation<Label, ApiError, { name: string; newName?: string; color?: string }>({
    mutationFn: ({ name, newName, color }) =>
      apiRequest<Label>('PATCH', labelUrl(name), {
        body: { name: newName ?? null, color: color ?? null },
      }),
    onSettled: refresh,
  });
}

/** Deletes a label: it is taken off every message that has it; the messages stay. */
export function useDeleteLabel() {
  const refresh = useRefreshAfterChange();
  return useMutation<void, ApiError, { name: string }>({
    mutationFn: ({ name }) => apiRequest<void>('DELETE', labelUrl(name)),
    onSettled: refresh,
  });
}

export interface LabelChange {
  ids: string[];
  add?: string[];
  remove?: string[];
}

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

function withChange(labels: string[], add: string[], remove: string[]): string[] {
  const kept = labels.filter((l) => !remove.some((r) => same(l, r)));
  const added = add.filter((a) => !kept.some((l) => same(l, a)));
  return [...kept, ...added].sort((a, b) => a.localeCompare(b));
}

/**
 * Puts labels on messages and takes them off. The screen shows the result at once and goes back (with a message in plain
 * words) if the server refuses. The messages are changed one after another: the server changes the labels of one message
 * per request.
 */
export function useSetMessageLabels() {
  const queryClient = useQueryClient();
  const refresh = useRefreshAfterChange();
  const toast = useToast();
  const { t } = useTranslation();
  return useMutation<void, ApiError, LabelChange, Snapshot>({
    mutationKey: ['message-labels'],
    scope: { id: 'message-labels' },
    mutationFn: async ({ ids, add = [], remove = [] }) => {
      for (const id of ids) {
        await apiRequest('PATCH', `/api/v1/messages/${id}/labels`, {
          body: { add: add.length > 0 ? add : null, remove: remove.length > 0 ? remove : null },
        });
      }
    },
    onMutate: async ({ ids, add = [], remove = [] }) => {
      await Promise.all([
        queryClient.cancelQueries({ queryKey: ['messages'] }),
        queryClient.cancelQueries({ queryKey: ['message'] }),
      ]);
      const snapshot = takeSnapshot(queryClient);
      const wanted = new Set(ids);
      queryClient.setQueriesData<InfiniteData<ListPage>>({ queryKey: ['messages'] }, (data) =>
        data
          ? {
              ...data,
              pages: data.pages.map((page) => ({
                ...page,
                items: page.items.map((item) =>
                  wanted.has(item.id)
                    ? { ...item, labels: withChange(item.labels, add, remove) }
                    : item,
                ),
              })),
            }
          : data,
      );
      for (const id of ids) {
        queryClient.setQueriesData<Detail>({ queryKey: ['message', id] }, (message) =>
          message ? { ...message, labels: withChange(message.labels, add, remove) } : message,
        );
      }
      return snapshot;
    },
    onError: (error, _change, snapshot) => {
      if (snapshot) restore(queryClient, snapshot);
      toast.show({ tone: 'error', message: describeApiError(error, t) });
    },
    onSettled: refresh,
  });
}
