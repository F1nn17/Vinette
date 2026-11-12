import { Flag, Paperclip } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { cn, formatListDate, labelColor } from '@/shared/lib';
import { Avatar, LabelTag } from '@/shared/ui';
import type { MessageItem } from './api';

export interface MessageRowProps {
  message: MessageItem;
  /** Where the row leads (the message's address, with the list's own parameters kept). */
  to: string;
  selected: boolean;
  /** Sent mail and drafts show who the message is to, not who it is from. */
  showRecipient: boolean;
  now: Date;
  /** Mobile: an avatar on the left and the unread dot beside the preview (DESIGN.md §8). */
  avatar?: boolean;
  /** The compact density: lower rows and no preview (DESIGN.md §5). */
  compact?: boolean;
  /** The colors the user chose for labels (by name); the others get one by their name. */
  labelColors?: Readonly<Record<string, string>>;
}

/** One line of the list (DESIGN.md §5): sender, subject, preview, labels; unread, flag and attachment marks. */
export function MessageRow({
  message,
  to,
  selected,
  showRecipient,
  now,
  avatar = false,
  compact = false,
  labelColors,
}: MessageRowProps) {
  const { t, i18n } = useTranslation();
  const person = showRecipient ? message.to[0] : message.from;
  const who = person ? person.name || person.address : t('list.noSender');
  const sender = showRecipient ? t('list.to', { name: who }) : who;
  const subject = message.subject || t('list.noSubject');
  const unread = !message.seen;
  const time = formatListDate(new Date(message.date), now, i18n.language, {
    yesterday: t('list.yesterday'),
  });

  const marks = (
    <>
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
    </>
  );
  const dot = (
    <span
      role="img"
      aria-label={t('list.unread')}
      className="size-2 shrink-0 rounded-full bg-accent"
    />
  );
  const labels = message.labels.length > 0 && (
    <span className="mt-1.5 flex flex-wrap gap-1.5">
      {message.labels.map((label) => (
        <LabelTag key={label} color={labelColor(label, labelColors)}>
          {label}
        </LabelTag>
      ))}
    </span>
  );
  const linkClass = cn(
    'block border-b border-subtle transition-colors',
    compact && !avatar ? 'py-[9px]' : 'py-3.5',
    selected ? 'bg-accent-soft' : 'hover:bg-bg',
  );

  if (avatar) {
    return (
      <Link
        to={to}
        aria-current={selected ? 'true' : undefined}
        className={cn(linkClass, 'flex items-start gap-3 px-4')}
      >
        <span aria-hidden="true" className="shrink-0">
          <Avatar name={who} size={40} tone="neutral" />
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-2">
            <span
              className={cn(
                'min-w-0 flex-1 truncate text-base text-ink',
                unread && 'font-semibold',
              )}
            >
              {sender}
            </span>
            {marks}
          </span>
          <span
            className={cn('mt-0.5 block truncate text-base text-ink', unread && 'font-semibold')}
          >
            {subject}
          </span>
          <span className="mt-0.5 flex items-center gap-2">
            <span className="min-w-0 flex-1 truncate text-secondary text-muted">
              {message.preview}
            </span>
            {unread && dot}
          </span>
          {labels}
        </span>
      </Link>
    );
  }

  return (
    <Link to={to} aria-current={selected ? 'true' : undefined} className={cn(linkClass, 'px-5')}>
      <span className="flex items-center gap-2">
        {unread && dot}
        <span
          className={cn('min-w-0 flex-1 truncate text-base text-ink', unread && 'font-semibold')}
        >
          {sender}
        </span>
        {marks}
      </span>
      <span className={cn('mt-0.5 block truncate text-base text-ink', unread && 'font-semibold')}>
        {subject}
      </span>
      {message.preview && !compact && (
        <span className="mt-0.5 block truncate text-secondary text-muted">{message.preview}</span>
      )}
      {labels}
    </Link>
  );
}
