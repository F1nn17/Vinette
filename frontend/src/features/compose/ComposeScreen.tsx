import { Check, ChevronDown, ChevronLeft, Send, Trash2 } from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { apiPost, apiRequest } from '@/shared/api/client';
import { describeApiError } from '@/shared/api/describe-error';
import type { components } from '@/shared/api/schema';
import { Confirm } from '@/features/mailbox';
import { SignaturesEditor, useSignatures } from '@/features/settings';
import { Button, Dialog } from '@/shared/ui';
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
import { RecipientsCard } from './RecipientsCard';
import { ScheduleDialog } from './ScheduleDialog';
import { formatWhen } from './schedule';
import { SignatureCard } from './SignatureCard';
import { withSignature } from './signature';

type Sent = components['schemas']['SentDto'];
type Scheduled = components['schemas']['ScheduleResponse'];

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
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const toast = useToast();
  const [values, setValues] = useState<ComposeValues>(start?.values ?? EMPTY_COMPOSE);
  const [body, setBody] = useState<ComposeBody>(start?.body ?? EMPTY_BODY);
  const attachments = useAttachments();
  // Until the list of uploads is read, an opened draft has the files it was opened with: reading the list is not a change of the draft.
  const attachmentIds =
    attachments.loading && start?.attachmentIds
      ? start.attachmentIds
      : attachments.items.flatMap((a) => (a.status === 'done' && a.id ? [a.id] : []));
  const [receipt, setReceipt] = useState(start?.receipt ?? false); // off unless the user asks (DESIGN.md §6)
  const content = buildDraftContent(
    values,
    body,
    attachmentIds,
    { inReplyTo: start?.inReplyTo ?? null, references: start?.references ?? null },
    receipt,
  );
  const draft = useDraftAutosave(content, undefined, start?.draft);
  const [busy, setBusy] = useState<'sending' | 'deleting' | null>(null);
  const busyRef = useRef(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [question, setQuestion] = useState<'subject' | 'delete' | null>(null);
  const signatures = useSignatures();
  // null: the user has not chosen, so the default signature goes under the message; an empty id is "without a signature".
  const [picked, setPicked] = useState<string | null>(null);
  const signatureId = picked ?? signatures.defaultSignature?.id ?? '';
  const signature = signatures.list.find((s) => s.id === signatureId) ?? null;
  const [managing, setManaging] = useState(false);
  const [later, setLater] = useState(false); // the "Send later" window
  const [askedAt, setAskedAt] = useState<Date | null>(null); // the time chosen, while the question about a missing subject is open

  const send = async (subjectChecked = false, sendAt?: Date) => {
    if (busyRef.current) return;
    const blocker = sendBlocker(values, attachments.items);
    if (blocker) {
      setProblem(t(`compose.problem.${blocker}`));
      return;
    }
    if (!subjectChecked && values.subject.trim() === '') {
      setAskedAt(sendAt ?? null);
      setQuestion('subject');
      return;
    }
    busyRef.current = true;
    setBusy('sending');
    setProblem(null);
    try {
      const draftId = await draft.settle();
      const message = withSignature(content as NonNullable<typeof content>, signature);
      if (sendAt) {
        const queued = await apiPost<Scheduled>('/api/v1/scheduled', {
          ...message,
          sendAt: sendAt.toISOString(),
          draftId,
        });
        toast.show({
          message: t('compose.schedule.queued', { when: formatWhen(sendAt, i18n.language) }),
          action: { label: t('toast.undo'), onSelect: () => void cancelQueued(queued.item.id) },
          duration: 15_000,
        });
      } else {
        const sent = await apiPost<Sent>('/api/v1/messages', { ...message, draftId });
        toast.show({
          message: t(sent.copy === 'saved' ? 'compose.sent' : 'compose.sentNoCopy'),
        });
      }
      void navigate('/mail/inbox');
      return; // the message is out and the screen is leaving: the button stays busy, so that it cannot send twice
    } catch (error) {
      draft.resume();
      setProblem(describeApiError(error, t));
    }
    busyRef.current = false;
    setBusy(null);
  };

  // "Undo" in the notice after a message was put off: it goes back among the drafts, and the notice says so (or that it did not work).
  const cancelQueued = async (id: string) => {
    try {
      await apiRequest<void>('DELETE', `/api/v1/scheduled/${encodeURIComponent(id)}`);
      toast.show({ message: t('compose.schedule.cancelled') });
    } catch (error) {
      toast.show({ message: describeApiError(error, t), tone: 'error' });
    }
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
      <main
        className="grid min-w-0 flex-1 content-start items-start gap-5 p-7 max-md:p-3 lg:grid-cols-[minmax(0,1fr)_320px]"
        onKeyDown={onKeyDown}
      >
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
            <div className="inline-flex">
              <Button
                onClick={() => void send()}
                disabled={busy !== null}
                size="lg"
                className="rounded-r-none"
                icon={<Send size={18} strokeWidth={1.8} aria-hidden />}
              >
                {t(busy === 'sending' ? 'compose.sending' : 'compose.send')}
              </Button>
              <Button
                aria-label={t('compose.later')}
                title={t('compose.later')}
                aria-haspopup="dialog"
                disabled={busy !== null}
                size="lg"
                className="rounded-l-none border-l border-on-accent/40 px-3"
                onClick={() => setLater(true)}
              >
                <ChevronDown size={18} strokeWidth={1.8} aria-hidden />
              </Button>
            </div>
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
        <aside aria-label={t('compose.options')} className="flex flex-col gap-5">
          <RecipientsCard recipients={[...values.to, ...values.cc, ...values.bcc]} />
          <label className="flex min-h-[52px] cursor-pointer items-center gap-3 rounded-xl border border-line bg-surface px-5 py-3 text-base text-text-2">
            <input
              type="checkbox"
              checked={receipt}
              onChange={(e) => setReceipt(e.target.checked)}
              className="size-[18px] shrink-0 cursor-pointer accent-accent"
            />
            {t('compose.receipt')}
          </label>
          <SignatureCard
            signatures={signatures.list}
            selectedId={signatureId}
            onSelect={setPicked}
            onManage={() => setManaging(true)}
          />
        </aside>
      </main>
      <Dialog
        open={managing}
        onClose={() => setManaging(false)}
        title={t('signatures.title')}
        closeLabel={t('common.close')}
        className="max-w-xl"
      >
        <SignaturesEditor onSaved={() => setManaging(false)} onCancel={() => setManaging(false)} />
      </Dialog>
      {later && (
        <ScheduleDialog
          open
          onClose={() => setLater(false)}
          onSchedule={(at) => {
            setLater(false);
            void send(false, at);
          }}
        />
      )}
      {question === 'subject' && (
        <Confirm
          title={t('compose.noSubjectTitle')}
          text={t('compose.noSubjectText')}
          confirm={t('compose.send')}
          onClose={() => setQuestion(null)}
          onConfirm={() => {
            setQuestion(null);
            void send(true, askedAt ?? undefined);
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
