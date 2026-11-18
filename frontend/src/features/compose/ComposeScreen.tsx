import { Check, ChevronLeft, Send, Trash2 } from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { apiPost, apiRequest } from '@/shared/api/client';
import { describeApiError } from '@/shared/api/describe-error';
import type { components } from '@/shared/api/schema';
import { Confirm } from '@/features/mailbox';
import { Button } from '@/shared/ui';
import { useToast } from '@/shared/toast';
import { EMPTY_COMPOSE, type ComposeValues } from './compose-values';
import { AttachButton } from './AttachButton';
import { AttachmentCards } from './AttachmentCards';
import { useAttachments } from './attachments';
import { ComposeForm } from './ComposeForm';
import { buildDraftContent, useDraftAutosave, type DraftStatus } from './draft';
import { EMPTY_BODY, type ComposeBody } from './editor-text';
import { MessageEditor } from './MessageEditor';
import type { ReplyStart } from './reply';
import { sendBlocker } from './send';

type Sent = components['schemas']['SentDto'];

export interface ComposeScreenProps {
  /** The mailbox the message goes from. */
  address: string;
  /** What the message starts with: an answer or a forward (VIN-102); absent for a new message. */
  start?: ReplyStart;
}

/**
 * The "New message" screen (DESIGN.md §6): a 64 px header, the form, the files and the footer with "Send" (also Ctrl+Enter) and
 * "Delete draft". Sending first stops the autosave and lets a save that is running finish, so that the draft is not written again after the
 * message is out; if the send fails the autosave goes on and the message stays on the screen with the reason in words.
 */
export function ComposeScreen({ address, start }: ComposeScreenProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const toast = useToast();
  const [values, setValues] = useState<ComposeValues>(start?.values ?? EMPTY_COMPOSE);
  const [body, setBody] = useState<ComposeBody>(start?.body ?? EMPTY_BODY);
  const attachments = useAttachments();
  const attachmentIds = attachments.items.flatMap((a) =>
    a.status === 'done' && a.id ? [a.id] : [],
  );
  const content = buildDraftContent(values, body, attachmentIds, {
    inReplyTo: start?.inReplyTo ?? null,
    references: start?.references ?? null,
  });
  const draft = useDraftAutosave(content);
  const [busy, setBusy] = useState<'sending' | 'deleting' | null>(null);
  const busyRef = useRef(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [question, setQuestion] = useState<'subject' | 'delete' | null>(null);

  const send = async (subjectChecked = false) => {
    if (busyRef.current) return;
    const blocker = sendBlocker(values, attachments.items);
    if (blocker) {
      setProblem(t(`compose.problem.${blocker}`));
      return;
    }
    if (!subjectChecked && values.subject.trim() === '') {
      setQuestion('subject');
      return;
    }
    busyRef.current = true;
    setBusy('sending');
    setProblem(null);
    try {
      const draftId = await draft.settle();
      const sent = await apiPost<Sent>('/api/v1/messages', { ...content, draftId });
      toast.show({
        message: t(sent.copy === 'saved' ? 'compose.sent' : 'compose.sentNoCopy'),
      });
      void navigate('/mail/inbox');
      return; // the message is out and the screen is leaving: the button stays busy, so that it cannot send twice
    } catch (error) {
      draft.resume();
      setProblem(describeApiError(error, t));
    }
    busyRef.current = false;
    setBusy(null);
  };

  const discard = async () => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy('deleting');
    setProblem(null);
    try {
      const draftId = await draft.settle();
      if (draftId) {
        await apiRequest<void>('DELETE', `/api/v1/drafts/${encodeURIComponent(draftId)}`);
      }
      for (const file of attachments.items) attachments.remove(file.key);
      toast.show({ message: t('compose.draftDeleted') });
      void navigate('/mail/inbox');
    } catch (error) {
      draft.resume();
      setProblem(describeApiError(error, t));
    } finally {
      busyRef.current = false;
      setBusy(null);
    }
  };

  // The newest `send`, for the key: it is called after the field that has the cursor has taken in what was typed in it.
  const sendNow = useRef(send);
  useEffect(() => {
    sendNow.current = send;
  });
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key !== 'Enter' || !(e.ctrlKey || e.metaKey) || e.nativeEvent.isComposing) return;
    e.preventDefault();
    // A recipient that is half typed is turned into a chip as the field loses the cursor; the message is sent from what is then on the screen.
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    setTimeout(() => void sendNow.current(), 0);
  };

  const askDelete = () => {
    // Nothing written, nothing to lose: leave without asking.
    if (content === null && attachments.items.length === 0) void navigate('/mail/inbox');
    else setQuestion('delete');
  };

  return (
    <div className="flex min-h-dvh flex-col bg-bg">
      <header className="flex h-16 shrink-0 items-center gap-4 border-b border-line bg-surface px-7 max-md:px-3">
        <Link
          to="/mail/inbox"
          aria-label={t('compose.backLabel')}
          className="inline-flex h-10 items-center gap-2 rounded-sm pr-3 pl-1 text-base font-medium text-text-2 hover:bg-bg max-md:h-11"
        >
          <ChevronLeft size={18} strokeWidth={1.8} aria-hidden />
          <span className="max-sm:sr-only">{t('compose.back')}</span>
        </Link>
        <span aria-hidden className="h-6 w-px bg-line" />
        <h1 className="text-logo font-semibold text-ink">{t('compose.title')}</h1>
        <DraftState status={draft.status} />
      </header>
      <main className="min-w-0 flex-1 p-7 max-md:p-3" onKeyDown={onKeyDown}>
        <ComposeForm address={address} values={values} onChange={setValues}>
          <MessageEditor value={body} onChange={setBody} autoFocus={start !== undefined} />
          <AttachmentCards items={attachments.items} onRemove={attachments.remove} />
          {problem && (
            <p
              role="alert"
              className="border-t border-subtle bg-warning-bg px-5 py-3 text-secondary text-warning"
            >
              {problem}
            </p>
          )}
          <div className="flex min-h-[68px] flex-wrap items-center gap-2 rounded-b-xl border-t border-subtle bg-surface-2 px-5 py-3">
            <Button
              onClick={() => void send()}
              disabled={busy !== null}
              size="lg"
              icon={<Send size={18} strokeWidth={1.8} aria-hidden />}
            >
              {t(busy === 'sending' ? 'compose.sending' : 'compose.send')}
            </Button>
            <AttachButton onFiles={attachments.add} />
            <span className="ml-1 text-meta text-muted max-md:hidden">{t('compose.sendHint')}</span>
            <button
              type="button"
              aria-label={t('compose.deleteDraft')}
              title={t('compose.deleteDraft')}
              disabled={busy !== null}
              onClick={askDelete}
              className="ml-auto inline-flex size-10 items-center justify-center rounded-sm text-text-2 transition-colors hover:bg-bg disabled:opacity-50 max-md:size-11"
            >
              <Trash2 size={18} strokeWidth={1.8} aria-hidden />
            </button>
          </div>
        </ComposeForm>
      </main>
      {question === 'subject' && (
        <Confirm
          title={t('compose.noSubjectTitle')}
          text={t('compose.noSubjectText')}
          confirm={t('compose.send')}
          onClose={() => setQuestion(null)}
          onConfirm={() => {
            setQuestion(null);
            void send(true);
          }}
        />
      )}
      {question === 'delete' && (
        <Confirm
          title={t('compose.deleteDraftTitle')}
          text={t('compose.deleteDraftText')}
          confirm={t('compose.deleteDraft')}
          onClose={() => setQuestion(null)}
          onConfirm={() => {
            setQuestion(null);
            void discard();
          }}
        />
      )}
    </div>
  );
}

/** "Draft saved at 14:41" with a green tick on the right of the header (DESIGN.md §6); a failure is said in words. */
function DraftState({ status }: { status: DraftStatus }) {
  const { t, i18n } = useTranslation();
  let content: ReactNode = null;
  if (status.state === 'saving') content = t('compose.draftSaving');
  else if (status.state === 'failed')
    content = <span className="text-warning">{t('compose.draftFailed')}</span>;
  else if (status.state === 'saved')
    content = (
      <>
        <Check size={18} strokeWidth={1.8} aria-hidden className="text-success" />
        {t('compose.draftSaved', {
          time: new Intl.DateTimeFormat(i18n.language, {
            hour: '2-digit',
            minute: '2-digit',
          }).format(status.at),
        })}
      </>
    );
  return (
    <p role="status" className="ml-auto flex items-center gap-1.5 text-secondary text-muted">
      {content}
    </p>
  );
}
