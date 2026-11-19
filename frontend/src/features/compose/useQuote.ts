import { queryOptions, useQuery } from '@tanstack/react-query';
import { apiGet } from '@/shared/api/client';
import type { Quote, ReplyMode } from './reply';

/**
 * How the message to answer or forward is read (`GET /messages/{id}/quote`). A forward asks for the message's files to be put among the
 * uploaded ones, which must happen once, so the answer is kept and never read again behind the user's back.
 */
export function quoteQuery(mode: ReplyMode, id: string) {
  return queryOptions({
    queryKey: ['quote', mode === 'forward' ? 'forward' : 'answer', id],
    queryFn: ({ signal }) =>
      apiGet<Quote>(
        `/api/v1/messages/${encodeURIComponent(id)}/quote${mode === 'forward' ? '?attachments=true' : ''}`,
        signal,
      ),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

export function useQuote(mode: ReplyMode, id: string) {
  return useQuery(quoteQuery(mode, id));
}
