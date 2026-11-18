import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { apiGet, apiRequest, apiUpload, ApiError } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

type Uploaded = components['schemas']['UploadedAttachmentDto'];
type ListResponse = components['schemas']['AttachmentsResponse'];

/** A file of the message, from the moment it is chosen. */
export interface AttachmentItem {
  /** Stands for the file in the list, whatever happens to it. */
  key: string;
  fileName: string;
  size: number;
  contentType: string;
  status: 'uploading' | 'done' | 'failed';
  /** How much of the file has gone, 0 to 1; 1 once it is done. */
  progress: number;
  /** The id the server gave the file, once it is there: the message refers to the file by it. */
  id?: string;
  /** Why the file is not attached (for `describeApiError`). */
  error?: unknown;
}

const LIST_KEY = ['attachments'] as const;

const tooBig = (code: string) => new ApiError(413, code, undefined, code);

const fromServer = (a: Uploaded): AttachmentItem => ({
  key: `server-${a.id}`,
  fileName: a.fileName,
  size: Number(a.size),
  contentType: a.contentType,
  status: 'done',
  progress: 1,
  id: a.id,
});

/**
 * The files of the message being written (VIN-099): the ones that already wait on the server (from before the page was opened), and the
 * ones being uploaded now, each with its progress. A file that cannot be attached is kept in the list with the reason, so that the user sees
 * what did not go and can take it off. The limits the server states are checked before a file is sent, so that a file that cannot be
 * accepted is not uploaded for nothing; the server checks them again.
 */
export function useAttachments() {
  const list = useQuery({
    queryKey: LIST_KEY,
    queryFn: ({ signal }) => apiGet<ListResponse>('/api/v1/attachments', signal),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const [local, setLocal] = useState<AttachmentItem[]>([]);
  const [hidden, setHidden] = useState<ReadonlySet<string>>(new Set());
  const controllers = useRef(new Map<string, AbortController>());
  const counter = useRef(0);

  // The files that wait on the server, apart from those this page has uploaded itself and those it has taken off.
  const ownIds = new Set(local.flatMap((a) => (a.id ? [a.id] : [])));
  const waiting = (list.data?.items ?? [])
    .filter((a) => !hidden.has(a.id) && !ownIds.has(a.id))
    .map(fromServer);
  const items = [...waiting, ...local];

  const itemsRef = useRef(items);
  useEffect(() => {
    itemsRef.current = items;
  });
  const limits = list.data?.limits;

  useEffect(() => {
    const running = controllers.current;
    return () => {
      for (const c of running.values()) c.abort();
    };
  }, []);

  const patch = useCallback((key: string, change: Partial<AttachmentItem>) => {
    setLocal((all) => all.map((a) => (a.key === key ? { ...a, ...change } : a)));
  }, []);

  const upload = useCallback(
    async (file: File, key: string) => {
      const controller = new AbortController();
      controllers.current.set(key, controller);
      try {
        const done = await apiUpload<Uploaded>('/api/v1/attachments', file, {
          signal: controller.signal,
          onProgress: (progress) => patch(key, { progress }),
        });
        patch(key, {
          status: 'done',
          progress: 1,
          id: done.id,
          fileName: done.fileName,
          contentType: done.contentType,
          size: Number(done.size),
        });
      } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') return;
        patch(key, { status: 'failed', error });
      } finally {
        controllers.current.delete(key);
      }
    },
    [patch],
  );

  const add = useCallback(
    (files: Iterable<File>) => {
      const fresh: AttachmentItem[] = [];
      const started: [File, string][] = [];
      // What counts against the limits: files that are there or on their way.
      const counted = itemsRef.current.filter((a) => a.status !== 'failed');
      let count = counted.length;
      let total = counted.reduce((sum, a) => sum + a.size, 0);
      for (const file of files) {
        counter.current += 1;
        const item: AttachmentItem = {
          key: `local-${counter.current}`,
          fileName: file.name,
          size: file.size,
          contentType: file.type,
          status: 'uploading',
          progress: 0,
        };
        let refusal: ApiError | undefined;
        if (limits) {
          if (file.size > Number(limits.maxFileBytes)) refusal = tooBig('attachment_too_large');
          else if (count >= Number(limits.maxFiles))
            refusal = new ApiError(409, 'too_many_attachments', undefined, '');
          else if (total + file.size > Number(limits.maxTotalBytes))
            refusal = tooBig('attachments_too_large');
        }
        if (refusal) {
          fresh.push({ ...item, status: 'failed', error: refusal });
        } else {
          count += 1;
          total += file.size;
          fresh.push(item);
          started.push([file, item.key]);
        }
      }
      if (fresh.length === 0) return;
      setLocal((all) => [...all, ...fresh]);
      for (const [file, key] of started) void upload(file, key);
    },
    [limits, upload],
  );

  const remove = useCallback((key: string) => {
    const item = itemsRef.current.find((a) => a.key === key);
    if (!item) return;
    controllers.current.get(key)?.abort(); // an upload on its way is stopped; the server keeps nothing of it
    controllers.current.delete(key);
    setLocal((all) => all.filter((a) => a.key !== key));
    if (item.id === undefined) return;
    const id = item.id;
    setHidden((all) => new Set(all).add(id));
    void apiRequest<void>('DELETE', `/api/v1/attachments/${encodeURIComponent(id)}`).catch(
      (error: unknown) => {
        if (error instanceof ApiError && error.status === 404) return; // already gone
        // The file is still there: show it again rather than let the user think it was taken off.
        if (item.key.startsWith('local-')) setLocal((all) => [...all, item]);
        setHidden((all) => {
          const next = new Set(all);
          next.delete(id);
          return next;
        });
      },
    );
  }, []);

  return { items, add, remove, limits };
}
