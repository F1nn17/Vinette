import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { apiPost, type ApiError } from '@/shared/api/client';
import { describeApiError } from '@/shared/api/describe-error';
import type { components } from '@/shared/api/schema';
import { useToast } from '@/shared/toast';
import { Confirm } from './Confirm';
import { FOLDERS_KEY } from './api';
import { useMessageBatch } from './batch';

/**
 * Deleting messages of the trash for good (VIN-079): asks first, because there is no way back, then does it like any other
 * action on messages (the rows go at once). `dialog` goes anywhere in the tree of the component that uses it.
 */
export function usePurge(): {
  ask: (ids: string[], onDone?: () => void) => void;
  dialog: ReactNode;
} {
  const { t } = useTranslation();
  const batch = useMessageBatch();
  const [pending, setPending] = useState<{ ids: string[]; onDone?: () => void } | null>(null);
  const close = () => setPending(null);
  return {
    ask: (ids, onDone) => setPending({ ids, onDone }),
    dialog: pending && (
      <Confirm
        title={t('purge.title', { count: pending.ids.length })}
        text={t('purge.text', { count: pending.ids.length })}
        confirm={t('purge.confirm')}
        onClose={close}
        onConfirm={() => {
          const { ids, onDone } = pending;
          close();
          batch.mutate({ ids, action: 'delete' });
          onDone?.();
        }}
      />
    ),
  };
}

type Emptied = components['schemas']['EmptyFolderResult'];

/**
 * Emptying the trash or the spam folder (VIN-079): asks first, deletes everything in the folder for good, and says it is done. The
 * folder is read again afterwards, so the list and the counters show what the server has.
 */
export function useEmptyFolder(): {
  ask: (folder: { path: string; role: string }) => void;
  dialog: ReactNode;
} {
  const { t } = useTranslation();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [folder, setFolder] = useState<{ path: string; role: string } | null>(null);
  const empty = useMutation<Emptied, ApiError, { path: string; role: string }>({
    mutationFn: ({ path }) => apiPost<Emptied>(`/api/v1/folders/${encodeURIComponent(path)}/empty`),
    onSuccess: (_result, { role }) =>
      toast.show({ message: t(role === 'junk' ? 'purge.spamEmptied' : 'purge.trashEmptied') }),
    onError: (error) => toast.show({ tone: 'error', message: describeApiError(error, t) }),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['messages'] }),
        queryClient.invalidateQueries({ queryKey: ['message'] }),
        queryClient.invalidateQueries({ queryKey: FOLDERS_KEY }),
      ]),
  });
  const spam = folder?.role === 'junk';
  return {
    ask: setFolder,
    dialog: folder && (
      <Confirm
        title={t(spam ? 'purge.emptySpamTitle' : 'purge.emptyTrashTitle')}
        text={t('purge.emptyText')}
        confirm={t('purge.confirm')}
        onClose={() => setFolder(null)}
        onConfirm={() => {
          empty.mutate(folder);
          setFolder(null);
        }}
      />
    ),
  };
}
