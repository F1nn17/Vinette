import { Flag, Paperclip } from 'lucide-react';
import { useRef, type MouseEvent } from 'react';
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
  /** The row is among the selected ones (VIN-073). */
  checked?: boolean;
  /** Some messages are selected: every row shows its box (and on a phone a tap selects instead of opening). */
  selecting?: boolean;
  /** Selects or unselects the row (`toggle`), or selects the range from the last clicked row (`range`); absent: no selection. */
  onSelect?: (how: 'toggle' | 'range') => void;
}

/** How long a finger stays on a row to select it (phones). */
const LONG_PRESS_MS = 500;

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
  checked = false,
  selecting = false,
  onSelect,
}: MessageRowProps) {
  const { t, i18n } = useTranslation();
  const press = useRef<{ timer: ReturnType<typeof setTimeout> | null; fired: boolean }>({
    timer: null,
    fired: false,
  });
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
    selected || checked ? 'bg-accent-soft' : 'hover:bg-bg',
  );

  // Ctrl/⌘ and Shift with a click select instead of opening; on a phone, while selecting, so does a tap (and a long press starts).
  const onRowClick = (event: MouseEvent) => {
    if (!onSelect) return;
    if (press.current.fired) {
      press.current.fired = false;
      event.preventDefault();
    } else if (event.ctrlKey || event.metaKey) {
      event.preventDefault();
      onSelect('toggle');
    } else if (event.shiftKey) {
      event.preventDefault();
      onSelect('range');
    } else if (avatar && selecting) {
      event.preventDefault();
      onSelect('toggle');
    }
  };
  const cancelPress = () => {
    if (press.current.timer) clearTimeout(press.current.timer);
    press.current.timer = null;
  };
  const touch =
    onSelect && avatar
      ? {
          onPointerDown: (event: { pointerType: string }) => {
            if (event.pointerType !== 'touch') return;
            cancelPress();
            press.current.timer = setTimeout(() => {
              press.current.fired = true;
              onSelect('toggle');
            }, LONG_PRESS_MS);
          },
          onPointerUp: cancelPress,
          onPointerLeave: cancelPress,
          onPointerCancel: cancelPress,
          onContextMenu: (event: MouseEvent) => {
            if (press.current.fired) event.preventDefault(); // no menu over a long press that selected
          },
        }
      : {};
  // The box sits in a 40 px area (DESIGN.md §11): at the left edge of the row on a computer (shown on hover, focus or while
  // selecting, so that the list looks as designed otherwise), in place of the avatar's left on a phone while selecting.
  const box = onSelect && (
    <label
      className={cn(
        'z-10 flex size-10 shrink-0 cursor-pointer items-center justify-center',
        !avatar && 'absolute top-1 -left-2.5',
        !avatar &&
          !selecting &&
          !checked &&
          'opacity-0 group-hover:opacity-100 focus-within:opacity-100',
      )}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={() => undefined}
        onClick={(event) => onSelect(event.shiftKey ? 'range' : 'toggle')}
        aria-label={t('selection.select', { subject })}
        className="size-4 cursor-pointer accent-accent"
      />
    </label>
  );

  if (avatar) {
    return (
      <div className="flex items-start">
        {selecting && (
          <span className="flex shrink-0 items-center self-stretch border-b border-subtle pl-2">
            {box}
          </span>
        )}
        <Link
          to={to}
          aria-current={selected ? 'true' : undefined}
          className={cn(linkClass, 'flex min-w-0 flex-1 items-start gap-3 px-4')}
          onClick={onRowClick}
          {...touch}
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
      </div>
    );
  }

  return (
    <div className="group relative">
      {box}
      <Link
        to={to}
        aria-current={selected ? 'true' : undefined}
        className={cn(linkClass, 'px-5')}
        onClick={onRowClick}
      >
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
    </div>
  );
}
