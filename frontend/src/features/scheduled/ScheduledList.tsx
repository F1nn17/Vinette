import { AlertTriangle, Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { cn } from '@/shared/lib';
import { NavMenuButton, useLayout } from '@/features/mailbox';
import { ListError, ListSkeleton } from '@/features/mailbox/list-states';
import { scheduledPath, summaryOf, useScheduled, type ScheduledItem } from './api';
import { problemText } from './problem';
import { fullWhen, shortWhen } from './when';

export interface ScheduledListProps {
  /** The message that is open (`/mail/@scheduled/<id>`), if any. */
  openId?: string;
}

function who(item: ScheduledItem, noRecipient: string): string {
  const first = item.to[0] ?? item.cc[0];
  return first ? first.name || first.address : noRecipient;
}

function Row({
  item,
  selected,
  now,
  mobile,
}: {
  item: ScheduledItem;
  selected: boolean;
  now: Date;
  mobile: boolean;
}) {
  const { t, i18n } = useTranslation();
  const failed = item.state === 'failed';
  const at = new Date(item.sendAt);
  const name = t('scheduled.to', { name: who(item, t('list.noSender')) });
  const when = shortWhen(at, now, i18n.language, {
    today: t('scheduled.today'),
    tomorrow: t('scheduled.tomorrow'),
  });
  return (
    <Link
      to={scheduledPath(item.id)}
      aria-current={selected ? 'true' : undefined}
      className={cn(
        'block border-b border-subtle py-3.5 transition-colors',
        mobile ? 'px-4' : 'px-5',
        selected ? 'bg-accent-soft' : 'hover:bg-bg',
      )}
    >
      <span className="flex items-center gap-2">
        <span className="min-w-0 flex-1 truncate text-base font-semibold text-ink">{name}</span>
        {failed ? (
          <span className="flex shrink-0 items-center gap-1 text-meta text-warning">
            <AlertTriangle size={14} strokeWidth={1.8} aria-hidden />
            {t('scheduled.failedShort')}
          </span>
        ) : (
          <span className="flex shrink-0 items-center gap-1 text-meta text-muted">
            <Clock size={14} strokeWidth={1.8} role="img" aria-label={t('scheduled.sendsAt')} />
            <time dateTime={item.sendAt}>{when}</time>
          </span>
        )}
      </span>
      <span className="mt-0.5 block truncate text-base text-ink">
        {item.subject || t('list.noSubject')}
      </span>
      <span
        className={cn(
          'mt-0.5 block truncate text-secondary',
          failed ? 'text-warning' : 'text-muted',
        )}
      >
        {failed
          ? problemText(item.problem, t)
          : t('scheduled.willSend', { when: fullWhen(at, i18n.language) })}
      </span>
    </Link>
  );
}

function Group({
  title,
  items,
  openId,
  now,
  mobile,
}: {
  title: string;
  items: ScheduledItem[];
  openId?: string;
  now: Date;
  mobile: boolean;
}) {
  return (
    <section aria-label={title}>
      <h2
        className={cn(
          'pt-3 pb-1.5 text-caps font-semibold tracking-[0.05em] text-muted uppercase',
          mobile ? 'px-4' : 'px-5',
        )}
      >
        {title}
      </h2>
      {items.map((item) => (
        <Row key={item.id} item={item} selected={item.id === openId} now={now} mobile={mobile} />
      ))}
    </section>
  );
}

/**
 * The list of the messages put off (DESIGN.md §5, "Отложенные"): first the ones that did not go, then the ones that wait, soonest first. Not a folder
 * of the mailbox but the queue of the application, so there is no search, no filter and no selection here.
 */
export function ScheduledList({ openId }: ScheduledListProps) {
  const { t } = useTranslation();
  const mobile = useLayout() === 'mobile';
  const query = useScheduled();
  const items = query.data ?? [];
  const { waiting } = summaryOf(items);
  const failed = items.filter((i) => i.state === 'failed');
  const pending = items.filter((i) => i.state === 'pending');
  const now = new Date();

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header className={cn('flex flex-col px-5 pb-3', mobile ? 'px-4 pt-3' : 'pt-[18px]')}>
        <div className="flex items-center gap-2">
          {mobile && <NavMenuButton />}
          <h1 className="flex min-w-0 flex-1 items-baseline gap-2">
            <span
              className={cn('truncate font-bold text-ink', mobile ? 'text-lead' : 'text-heading')}
              style={mobile ? { fontSize: 20 } : undefined}
            >
              {t('scheduled.title')}
            </span>
            {waiting > 0 && !mobile && (
              <span className="shrink-0 text-secondary text-muted">
                {t('scheduled.waitingCount', { count: waiting })}
              </span>
            )}
          </h1>
        </div>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {query.isPending && <ListSkeleton rows={4} />}
        {query.isError && <ListError error={query.error} onRetry={() => void query.refetch()} />}
        {query.isSuccess && items.length === 0 && (
          <p className="p-5 text-muted">{t('scheduled.empty')}</p>
        )}
        {failed.length > 0 && (
          <Group
            title={t('scheduled.groupFailed')}
            items={failed}
            openId={openId}
            now={now}
            mobile={mobile}
          />
        )}
        {pending.length > 0 && (
          <Group
            title={t('scheduled.groupWaiting')}
            items={pending}
            openId={openId}
            now={now}
            mobile={mobile}
          />
        )}
      </div>
    </div>
  );
}
