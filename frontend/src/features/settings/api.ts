import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiRequest } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

export type Settings = components['schemas']['SettingsDto'];
export type ListDensity = 'comfortable' | 'compact';

/** The languages the application has; no choice (an empty one) follows the browser. */
export const LANGUAGES = ['ru', 'en'] as const;

/** The language to show for a choice: the chosen one, or, for none, the browser's if the application has it, else Russian. */
export function languageFor(
  choice: string | null | undefined,
  browser: string = navigator.language,
): string {
  const own = LANGUAGES.find((l) => l === choice);
  if (own) return own;
  return LANGUAGES.find((l) => browser.toLowerCase().startsWith(l)) ?? 'ru';
}

export const SETTINGS_KEY = ['settings'] as const;

/** What the user has chosen in the interface (list density, language, label colors). */
export function useSettings() {
  return useQuery({
    queryKey: SETTINGS_KEY,
    queryFn: ({ signal }) => apiGet<Settings>('/api/v1/settings', signal),
  });
}

/** The list density; the comfortable one until the settings are loaded (or if they cannot be). */
export function useListDensity(): ListDensity {
  const { data } = useSettings();
  return data?.density === 'compact' ? 'compact' : 'comfortable';
}

/**
 * Changes settings: the screen follows at once, and goes back if the server refuses (the user sees the old choice again
 * rather than a state that was never saved).
 */
export function useUpdateSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (change: { density?: ListDensity; language?: string }) =>
      apiRequest<Settings>('PATCH', '/api/v1/settings', {
        // Only what is present is changed; an empty language goes back to following the browser.
        body: {
          density: change.density ?? null,
          language: change.language ?? null,
          labelColors: null,
        },
      }),
    onMutate: async (change) => {
      await queryClient.cancelQueries({ queryKey: SETTINGS_KEY });
      const before = queryClient.getQueryData<Settings>(SETTINGS_KEY);
      if (before) {
        queryClient.setQueryData<Settings>(SETTINGS_KEY, {
          ...before,
          density: change.density ?? before.density,
          language: change.language === undefined ? before.language : change.language || null,
        });
      }
      return { before };
    },
    onError: (_error, _change, context) => {
      if (context?.before) queryClient.setQueryData(SETTINGS_KEY, context.before);
    },
    onSuccess: (saved) => queryClient.setQueryData(SETTINGS_KEY, saved),
  });
}

/**
 * Takes a sender off the list whose pictures are shown without asking. The open messages are loaded again, as what they show depends on it.
 * (The other direction, adding, is `useTrustImageSender` of the message, where the choice is made.)
 */
export function useUntrustImageSender() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (address: string) =>
      apiRequest<Settings>('PATCH', '/api/v1/settings', { body: { untrustImagesFrom: address } }),
    onSuccess: async (saved) => {
      queryClient.setQueryData(SETTINGS_KEY, saved);
      await queryClient.invalidateQueries({ queryKey: ['message'] });
    },
  });
}
