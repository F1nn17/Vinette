import { Check, ChevronLeft } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { EMPTY_COMPOSE, type ComposeValues } from './compose-values';
import { AttachButton } from './AttachButton';
import { AttachmentCards } from './AttachmentCards';
import { useAttachments } from './attachments';
import { ComposeForm } from './ComposeForm';
import { buildDraftContent, useDraftAutosave, type DraftStatus } from './draft';
import { EMPTY_BODY, type ComposeBody } from './editor-text';
import { MessageEditor } from './MessageEditor';

export interface ComposeScreenProps {
  /** The mailbox the message goes from. */
  address: string;
}

/** The "New message" screen (DESIGN.md §6): a 64 px header and the form. */
export function ComposeScreen({ address }: ComposeScreenProps) {
  const { t } = useTranslation();
  const [values, setValues] = useState<ComposeValues>(EMPTY_COMPOSE);
  const [body, setBody] = useState<ComposeBody>(EMPTY_BODY);
  const attachments = useAttachments();
  const attachmentIds = attachments.items.flatMap((a) =>
    a.status === 'done' && a.id ? [a.id] : [],
  );
  const draft = useDraftAutosave(buildDraftContent(values, body, attachmentIds));

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
        <DraftState status={draft} />
      </header>
      <main className="min-w-0 flex-1 p-7 max-md:p-3">
        <ComposeForm address={address} values={values} onChange={setValues}>
          <MessageEditor value={body} onChange={setBody} />
          <AttachmentCards items={attachments.items} onRemove={attachments.remove} />
          <div className="flex min-h-[68px] items-center gap-2 rounded-b-xl border-t border-subtle bg-surface-2 px-5 py-3">
            <AttachButton onFiles={attachments.add} />
          </div>
        </ComposeForm>
      </main>
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
