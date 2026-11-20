import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiGet } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

type Lookup = components['schemas']['RecipientLookupResponse'];

/** What is known of one person: when the newest message with them is dated, or null for somebody the mailbox has no mail with. */
export type KnownMap = ReadonlyMap<string, string | null>;

/**
 * What the mailbox knows of the people of the message (`GET /recipients/lookup`), by lower-case address. Empty while the answer is on its
 * way, so that the card shows the people at once and their captions a moment later; also empty if it cannot be read (the captions are only
 * a hint, never worth an error on the screen).
 */
export function useRecipientLookup(addresses: string[]): KnownMap {
  const wanted = [...new Set(addresses.map((a) => a.trim().toLowerCase()))].sort();
  const query = useQuery({
    queryKey: ['recipient-lookup', wanted],
    queryFn: async ({ signal }) => {
      const asked = wanted.map((a) => `address=${encodeURIComponent(a)}`).join('&');
      return (await apiGet<Lookup>(`/api/v1/recipients/lookup?${asked}`, signal)).items;
    },
    enabled: wanted.length > 0,
    staleTime: 60_000,
    retry: false,
    placeholderData: keepPreviousData,
  });
  return new Map((query.data ?? []).map((i) => [i.address.toLowerCase(), i.lastMail]));
}
