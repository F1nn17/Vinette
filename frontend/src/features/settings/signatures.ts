import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiRequest } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';
import { SETTINGS_KEY, useSettings, type Settings } from './api';

export type Signature = components['schemas']['SignatureDto'];
export type SignatureInput = components['schemas']['SignatureInput'];

export interface Signatures {
  /** The signatures in the order they are listed; empty until the settings are loaded. */
  list: Signature[];
  /** The one that goes under new messages unless another is chosen; null if none is. */
  defaultSignature: Signature | null;
  /** The settings are still being read: the default is not known yet. */
  loading: boolean;
}

/** The user's signatures (they live among the settings, ADR-0004). */
export function useSignatures(): Signatures {
  const { data, isPending } = useSettings();
  const list = data?.signatures ?? [];
  return { list, defaultSignature: list.find((s) => s.default) ?? null, loading: isPending };
}

/**
 * Saves the whole list of signatures (the ones to keep with their ids, new ones without; the one marked `default` is the default). The
 * answer carries the ids of the new ones.
 */
export function useSaveSignatures() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (signatures: SignatureInput[]) =>
      apiRequest<Settings>('PATCH', '/api/v1/settings', {
        body: { density: null, language: null, labelColors: null, signatures },
      }),
    onSuccess: (saved) => queryClient.setQueryData(SETTINGS_KEY, saved),
  });
}
