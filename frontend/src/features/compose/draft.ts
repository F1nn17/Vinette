import { useEffect, useRef, useState } from 'react';
import { apiPost } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';
import type { ComposeBody } from './editor-text';
import type { ComposeValues } from './compose-values';
import { isValidAddress, type Recipient } from './recipients';

type DraftRequest = components['schemas']['DraftRequest'];
type DraftSaved = components['schemas']['DraftSavedDto'];

/** What of the message goes into the draft: the request without the two fields that say which version it replaces. */
export type DraftContent = Omit<DraftRequest, 'previousId' | 'messageId'>;

/** The pause after the last change before the draft is saved. */
export const AUTOSAVE_DELAY_MS = 1500;

const addresses = (people: Recipient[]) =>
  people
    .filter((r) => isValidAddress(r.address))
    .map((r) => ({ name: r.name, address: r.address }));

/**
 * The draft for what is on the screen, or null while there is nothing to keep (no recipient, subject, text or file). A recipient that
 * is not an address is left out: the server refuses the whole draft for it, and the user is shown the mistake in the field anyway.
 */
export function buildDraftContent(
  values: ComposeValues,
  body: ComposeBody,
  attachmentIds: string[],
): DraftContent | null {
  const empty =
    values.to.length === 0 &&
    values.cc.length === 0 &&
    values.bcc.length === 0 &&
    values.subject.trim() === '' &&
    body.text.trim() === '' &&
    attachmentIds.length === 0;
  if (empty) return null;
  return {
    fromName: null,
    to: addresses(values.to),
    cc: addresses(values.cc),
    bcc: addresses(values.bcc),
    subject: values.subject,
    // A plain message goes as text only; a formatted one as HTML (the server makes the text version from it).
    text: body.plain ? body.text : null,
    html: body.plain || body.html === '' ? null : body.html,
    attachmentIds,
    inReplyTo: null,
    references: null,
  };
}

export type DraftStatus =
  { state: 'idle' } | { state: 'saving' } | { state: 'saved'; at: Date } | { state: 'failed' };

/**
 * Keeps the draft of the message on the server (VIN-094) while it is written (VIN-100): saved a moment after the last change, one save at a time
 * (a change made during a save is saved right after it), and at once when the page is hidden or closed or the screen is left, so that closing
 * the tab does not lose the text. While there is a change that is not saved yet, the browser asks before the page is closed.
 * Each save replaces the previous version of the draft; the ids that tell which one that is are kept here.
 */
export function useDraftAutosave(
  content: DraftContent | null,
  delay = AUTOSAVE_DELAY_MS,
): DraftStatus {
  const [status, setStatus] = useState<DraftStatus>({ state: 'idle' });
  const key = content === null ? '' : JSON.stringify(content);
  const latest = useRef({ content, key });
  const saved = useRef('');
  const running = useRef(false);
  const ids = useRef<{ previousId: string | null; messageId: string | null }>({
    previousId: null,
    messageId: null,
  });

  useEffect(() => {
    latest.current = { content, key };
  });

  // Saves until what is on the server is what is on the screen.
  const save = useRef(async (keepalive: boolean) => {
    if (running.current) return; // the running save looks again when it is done
    running.current = true;
    try {
      while (latest.current.content !== null && latest.current.key !== saved.current) {
        const { content: now, key: nowKey } = latest.current;
        setStatus({ state: 'saving' });
        try {
          const result = await apiPost<DraftSaved>(
            '/api/v1/drafts',
            { ...now, ...ids.current },
            undefined,
            keepalive,
          );
          ids.current = { previousId: result.id, messageId: result.messageId };
          saved.current = nowKey;
          setStatus({ state: 'saved', at: new Date(result.savedAt) });
        } catch {
          setStatus({ state: 'failed' });
          return; // the next change tries again
        }
      }
    } finally {
      running.current = false;
    }
  });

  // Saved a moment after the last change.
  useEffect(() => {
    if (key === '' || key === saved.current) return;
    const timer = setTimeout(() => void save.current(false), delay);
    return () => clearTimeout(timer);
  }, [key, delay]);

  // Saved at once when the page goes away or the screen is left.
  useEffect(() => {
    const flush = () => void save.current(true);
    const onHide = () => {
      if (document.visibilityState === 'hidden') flush();
    };
    const onLeave = (e: BeforeUnloadEvent) => {
      const unsaved = latest.current.content !== null && latest.current.key !== saved.current;
      if (unsaved || running.current) e.preventDefault();
    };
    document.addEventListener('visibilitychange', onHide);
    window.addEventListener('pagehide', flush);
    window.addEventListener('beforeunload', onLeave);
    return () => {
      document.removeEventListener('visibilitychange', onHide);
      window.removeEventListener('pagehide', flush);
      window.removeEventListener('beforeunload', onLeave);
      flush();
    };
  }, []);

  return status;
}
