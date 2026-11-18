import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { apiGet } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';
import type { Recipient } from './recipients';

/** The server's limit on what is looked for. */
const MAX_QUERY_LENGTH = 100;
const TYPING_PAUSE_MS = 150;

/** `text`, but only after it has stayed the same for a moment: no request for every key. */
function useSettled(text: string): string {
  const [settled, setSettled] = useState(text);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(text), TYPING_PAUSE_MS);
    return () => clearTimeout(timer);
  }, [text]);
  return settled;
}

/** The people to offer for what has been typed so far (the mailbox's own correspondents, best first); none while nothing is typed. */
export function useRecipientSuggestions(typed: string): Recipient[] {
  const query = useSettled(typed.trim()).slice(0, MAX_QUERY_LENGTH);
  const result = useQuery({
    queryKey: ['recipient-suggestions', query],
    queryFn: async ({ signal }) =>
      (
        await apiGet<components['schemas']['RecipientSuggestionsResponse']>(
          `/api/v1/recipients/suggestions?q=${encodeURIComponent(query)}`,
          signal,
        )
      ).items,
    enabled: query !== '',
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  });
  // What was typed since may no longer match what came back: nothing is offered for an empty field.
  return typed.trim() === '' ? [] : (result.data ?? []);
}
