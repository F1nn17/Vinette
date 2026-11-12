import { ImageOff, MailOpen } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { describeApiError } from '@/shared/api/describe-error';
import { formatMessageDate, labelColor } from '@/shared/lib';
import { Avatar, Button, LabelTag } from '@/shared/ui';
import { useMessage, verdictOf, type MessageDetail } from './api';
import { AttachmentList } from './AttachmentList';
import { useSetFlags } from './flags';
import { MessageFrame } from './MessageFrame';
import { ReaderActionBar } from './ReaderActionBar';
import { SenderCheckBadge } from './SenderCheckBadge';

export interface ReadingPaneProps {
  /** The open message; none shows a hint. */
  messageId?: string;
  /** The signed-in address: "To: you" is written when the message is addressed to it. */
  address?: string;
  /** On a phone the message fills the screen; this is what the "back" button does. Absent where the list is beside it. */
  onBack?: () => void;
}

function EmptyState() {
  const { t } = useTranslation();
  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 p-8 text-center text-muted">
      <MailOpen size={40} strokeWidth={1.4} aria-hidden />
      <p>{t('mailbox.noMessage')}</p>
    </div>
  );
}

function Skeleton() {
  const { t } = useTranslation();
  return (
    <div
      role="status"
      aria-label={t('common.loading')}
      className="mx-auto w-full max-w-[760px] px-10 py-7"
    >
      <div className="h-7 w-2/3 animate-pulse rounded-xs bg-line" />
      <div className="mt-6 flex items-center gap-3">
        <div className="size-[42px] animate-pulse rounded-full bg-line" />
        <div className="flex-1 space-y-2">
          <div className="h-4 w-1/3 animate-pulse rounded-xs bg-line" />
          <div className="h-3 w-1/2 animate-pulse rounded-xs bg-line/70" />
        </div>
      </div>
      <div className="mt-8 space-y-3">
        {[100, 92, 96, 70].map((width) => (
          <div
            key={width}
            className="h-4 animate-pulse rounded-xs bg-line/60"
            style={{ width: `${width}%` }}
          />
        ))}
      </div>
    </div>
  );
}

function recipients(
  message: MessageDetail,
  address: string | undefined,
  t: (key: string, options?: Record<string, unknown>) => string,
): string {
  const to = message.to;
  if (to.length === 0) return t('reader.noRecipients');
  const mine = address?.toLowerCase();
  if (to.length === 1 && mine !== undefined && to[0]?.address.toLowerCase() === mine) {
    return t('reader.toYou', { address: to[0].address });
  }
  const names = to.slice(0, 3).map((a) => a.name || a.address);
  const more = to.length - names.length;
  return more > 0
    ? t('reader.toMany', { names: names.join(', '), count: more })
    : t('reader.to', { names: names.join(', ') });
}

function Header({ message, address }: { message: MessageDetail; address?: string }) {
  const { t, i18n } = useTranslation();
  const sender = message.from;
  const name = sender ? sender.name || sender.address : t('list.noSender');
  const when = message.date
    ? formatMessageDate(new Date(message.date), new Date(), i18n.language)
    : null;
  const verdict = verdictOf(message);
  const blocked = Number(message.blockedExternalResources);
  return (
    <header className="flex flex-col gap-4">
      <div>
        <h1 id="reader-subject" className="text-subject font-bold text-ink">
          {message.subject || t('list.noSubject')}
        </h1>
        {message.labels.length > 0 && (
          <div className="mt-2 flex flex-wrap gap-1.5">
            {message.labels.map((label) => (
              <LabelTag key={label} color={labelColor(label)}>
                {label}
              </LabelTag>
            ))}
          </div>
        )}
      </div>

      <div className="flex items-start gap-3">
        <span aria-hidden="true">
          <Avatar name={name} size={42} />
        </span>
        <div className="min-w-0 flex-1">
          <p className="truncate">
            <span className="text-message font-semibold text-ink">{name}</span>
            {sender && sender.name && (
              <span className="ml-2 text-secondary text-muted">{sender.address}</span>
            )}
          </p>
          <p className="truncate text-secondary text-muted">{recipients(message, address, t)}</p>
        </div>
        {when && (
          <time dateTime={message.date ?? undefined} className="shrink-0 text-secondary text-muted">
            {when}
          </time>
        )}
      </div>

      <SenderCheckBadge verdict={verdict} />

      {blocked > 0 && (
        <div className="flex items-center gap-3 rounded-md bg-bg px-4 py-3 text-secondary text-text-2">
          <ImageOff size={18} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
          <p>{t('reader.imagesBlocked')}</p>
        </div>
      )}
    </header>
  );
}

/**
 * The reading area (DESIGN.md §5): the action bar, then subject, labels, sender, the sender check, the notice about hidden
 * pictures and the body in its sandboxed frame. The attachments follow the body; the quick reply (VIN-103) comes later.
 */
export function ReadingPane({ messageId, address, onBack }: ReadingPaneProps) {
  const { t } = useTranslation();
  const query = useMessage(messageId);
  const flags = useSetFlags(messageId);
  const markedRead = useRef<string | null>(null);
  const [failedFor, setFailedFor] = useState<string | null>(null); // the message whose flag could not be saved
  const flagFailed = failedFor === messageId;

  // Opening an unread message reads it. Once per message: if the server refuses, the mark goes back and is not forced again.
  const opened = query.data;
  const { mutate } = flags;
  useEffect(() => {
    if (messageId !== undefined && opened && !opened.seen && markedRead.current !== messageId) {
      markedRead.current = messageId;
      mutate({ seen: true });
    }
  }, [messageId, opened, mutate]);

  if (messageId === undefined) return <EmptyState />;

  if (query.isPending) {
    return (
      <div className="flex h-full flex-col">
        <ReaderActionBar flagged={false} onBack={onBack} />
        <Skeleton />
      </div>
    );
  }

  if (query.isError) {
    return (
      <div className="flex h-full flex-col">
        <ReaderActionBar flagged={false} onBack={onBack} />
        <div
          role="alert"
          className="mx-auto flex w-full max-w-[760px] flex-col items-start gap-3 px-10 py-7 text-text-2"
        >
          <p>{describeApiError(query.error, t)}</p>
          <Button variant="secondary" size="sm" onClick={() => void query.refetch()}>
            {t('common.retry')}
          </Button>
        </div>
      </div>
    );
  }

  const message = query.data;
  return (
    <article aria-labelledby="reader-subject" className="flex h-full flex-col">
      <ReaderActionBar
        onBack={onBack}
        flagged={message.flagged}
        onToggleFlag={() =>
          flags.mutate(
            { flagged: !message.flagged },
            { onSuccess: () => setFailedFor(null), onError: () => setFailedFor(messageId) },
          )
        }
      />
      {flagFailed && (
        <p
          role="alert"
          className="shrink-0 bg-warning-bg px-7 py-2 text-secondary text-warning max-md:px-4"
        >
          {t('reader.flagFailed')}
        </p>
      )}
      <div className="min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex min-h-full w-full max-w-[760px] flex-col gap-5 px-10 py-7 max-md:px-4">
          <Header message={message} address={address} />
          <div className="flex min-h-80 flex-1 flex-col">
            {message.bodyFormat === 'empty' ? (
              <p className="text-muted">{t('reader.emptyBody')}</p>
            ) : (
              <MessageFrame
                title={message.subject || t('list.noSubject')}
                body={message.body}
                format={message.bodyFormat === 'text' ? 'text' : 'html'}
              />
            )}
          </div>
          <AttachmentList messageId={message.id} attachments={message.attachments} />
        </div>
      </div>
    </article>
  );
}
