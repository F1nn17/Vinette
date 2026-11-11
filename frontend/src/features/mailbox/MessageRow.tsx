import { Flag, Paperclip } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { cn, formatListDate } from '@/shared/lib';
import { LabelTag } from '@/shared/ui';
import type { MessageItem } from './api';
import { labelColor } from './label-color';

export interface MessageRowProps {
  message: MessageItem;
  /** Where the row leads (the message's address, with the list's own parameters kept). */
  to: string;
  selected: boolean;
  /** Sent mail and drafts show who the message is to, not who it is from. */
  showRecipient: boolean;
  now: Date;
}

/** One line of the list (DESIGN.md §5): sender, subject, preview, labels; unread, flag and attachment marks. */
export function MessageRow({ message, to, selected, showRecipient, now }: MessageRowProps) {
  const { t, i18n } = useTranslation();
  const person = showRecipient ? message.to[0] : message.from;
  const who = person ? person.name || person.address : t('list.noSender');
  const sender = showRecipient ? t('list.to', { name: who }) : who;
  const subject = message.subject || t('list.noSubject');
  const unread = !message.seen;
  const time = formatListDate(new Date(message.date), now, i18n.language, {
    yesterday: t('list.yesterday'),
  });

  return (
    <Link
      to={to}
      aria-current={selected ? 'true' : undefined}
      className={cn(
        'block border-b border-subtle px-5 py-3.5 transition-colors',
        selected ? 'bg-accent-soft' : 'hover:bg-bg',
      )}
    >
      <span className="flex items-center gap-2">
        {unread && (
          <span
            role="img"
            aria-label={t('list.unread')}
            className="size-2 shrink-0 rounded-full bg-accent"
          />
        )}
        <span
          className={cn('min-w-0 flex-1 truncate text-base text-ink', unread && 'font-semibold')}
        >
          {sender}
        </span>
        {message.flagged && (
          <Flag
            size={14}
            strokeWidth={1.8}
            role="img"
            aria-label={t('list.flagged')}
            className="shrink-0 fill-flag text-flag"
          />
        )}
        {message.hasAttachments && (
          <Paperclip
            size={14}
            strokeWidth={1.8}
            role="img"
            aria-label={t('list.attachments')}
            className="shrink-0 text-muted"
          />
        )}
        <time dateTime={message.date} className="shrink-0 text-meta text-muted">
          {time}
        </time>
      </span>
      <span className={cn('mt-0.5 block truncate text-base text-ink', unread && 'font-semibold')}>
        {subject}
      </span>
      {message.preview && (
        <span className="mt-0.5 block truncate text-secondary text-muted">{message.preview}</span>
      )}
      {message.labels.length > 0 && (
        <span className="mt-1.5 flex flex-wrap gap-1.5">
          {message.labels.map((label) => (
            <LabelTag key={label} color={labelColor(label)}>
              {label}
            </LabelTag>
          ))}
        </span>
      )}
    </Link>
  );
}
