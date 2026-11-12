import { useQuery } from '@tanstack/react-query';
import { apiGet } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

export type MessageDetail = components['schemas']['MessageDetailDto'];
export type MessageAttachment = components['schemas']['AttachmentDto'];
export type SenderVerdict = 'verified' | 'suspicious' | 'unknown';

export const messageKey = (id: string) => ['message', id] as const;

/** One message opened for reading: headers, the cleaned body, the sender check, the attachments. */
export function useMessage(id: string | undefined) {
  return useQuery({
    queryKey: messageKey(id ?? ''),
    enabled: id !== undefined,
    queryFn: ({ signal }) =>
      apiGet<MessageDetail>(`/api/v1/messages/${encodeURIComponent(id ?? '')}`, signal),
    // A message does not change; its flags do, and they are updated by the actions that change them.
    staleTime: 5 * 60_000,
  });
}

export function verdictOf(message: MessageDetail): SenderVerdict {
  return message.sender.verdict === 'verified' || message.sender.verdict === 'suspicious'
    ? message.sender.verdict
    : 'unknown';
}
