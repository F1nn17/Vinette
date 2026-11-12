import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiRequest } from '@/shared/api/client';
import { SETTINGS_KEY, type Settings } from '@/features/settings';
import type { components } from '@/shared/api/schema';

export type MessageDetail = components['schemas']['MessageDetailDto'];
export type MessageAttachment = components['schemas']['AttachmentDto'];
export type SenderVerdict = 'verified' | 'suspicious' | 'unknown';

/** The key of a message; the whole-body variant has the same start, so one change of marks reaches both. */
export const messageKey = (id: string) => ['message', id] as const;

/**
 * One message opened for reading: headers, the cleaned body, the sender check, the attachments. A very long body comes
 * cut; with `full` the server sends as much of it as it ever shows. With `images` it fetches the pictures the message
 * takes from the web (the reader's browser never does) and puts them into the body.
 */
export function useMessage(
  id: string | undefined,
  { full = false, images = false }: { full?: boolean; images?: boolean } = {},
) {
  const flags = [full && 'full', images && 'images'].filter(Boolean) as string[];
  const query = [full && 'full=true', images && 'images=true'].filter(Boolean).join('&');
  return useQuery({
    queryKey: [...messageKey(id ?? ''), ...flags],
    enabled: id !== undefined,
    queryFn: ({ signal }) =>
      apiGet<MessageDetail>(
        `/api/v1/messages/${encodeURIComponent(id ?? '')}${query ? `?${query}` : ''}`,
        signal,
      ),
    // Showing the whole body replaces the beginning without a blank screen in between (never another message in its place).
    placeholderData: (previous) => (previous?.id === id ? previous : undefined),
    // A message does not change; its flags do, and they are updated by the actions that change them.
    staleTime: 5 * 60_000,
  });
}

/**
 * Chooses whether the pictures of a sender are shown without asking (kept in the settings). The open messages are loaded
 * again, as what they show depends on it; the list of settings follows the answer of the server.
 */
export function useTrustImageSender() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ address, trusted }: { address: string; trusted: boolean }) =>
      apiRequest<Settings>('PATCH', '/api/v1/settings', {
        body: trusted ? { trustImagesFrom: address } : { untrustImagesFrom: address },
      }),
    onSuccess: async (saved) => {
      queryClient.setQueryData(SETTINGS_KEY, saved);
      await queryClient.invalidateQueries({ queryKey: ['message'] });
    },
  });
}

export function verdictOf(message: MessageDetail): SenderVerdict {
  return message.sender.verdict === 'verified' || message.sender.verdict === 'suspicious'
    ? message.sender.verdict
    : 'unknown';
}
