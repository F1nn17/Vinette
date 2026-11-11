import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiRequest } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

export type Settings = components['schemas']['SettingsDto'];
export type ListDensity = 'comfortable' | 'compact';

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
    mutationFn: (change: { density?: ListDensity }) =>
      apiRequest<Settings>('PATCH', '/api/v1/settings', {
        body: { density: change.density ?? null, language: null, labelColors: null },
      }),
    onMutate: async (change) => {
      await queryClient.cancelQueries({ queryKey: SETTINGS_KEY });
      const before = queryClient.getQueryData<Settings>(SETTINGS_KEY);
      if (before && change.density) {
        queryClient.setQueryData<Settings>(SETTINGS_KEY, { ...before, density: change.density });
      }
      return { before };
    },
    onError: (_error, _change, context) => {
      if (context?.before) queryClient.setQueryData(SETTINGS_KEY, context.before);
    },
    onSuccess: (saved) => queryClient.setQueryData(SETTINGS_KEY, saved),
  });
}
