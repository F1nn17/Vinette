import { useQuery } from '@tanstack/react-query';
import { apiGet } from '@/shared/api/client';
import type { OpenedDraft } from './draftStart';

/**
 * A draft opened to be continued (`GET /drafts/{id}`). The server puts the draft's files among the uploaded ones when it is opened, which must
 * happen once, so the answer is kept and never read again behind the user's back.
 */
export function useDraft(id: string) {
  return useQuery({
    queryKey: ['draft', id],
    queryFn: ({ signal }) =>
      apiGet<OpenedDraft>(`/api/v1/drafts/${encodeURIComponent(id)}`, signal),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}
