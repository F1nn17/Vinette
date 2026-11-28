import { ArrowLeft, MailOpen } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { describeApiError } from '@/shared/api/describe-error';
import { useToast } from '@/shared/toast';
import { Button, IconButton } from '@/shared/ui';
import { useLayout } from '@/features/mailbox';
import { scheduledPath, useCancelScheduled, useScheduled, type ScheduledItem } from './api';
import { problemText } from './problem';
import { fullWhen } from './when';
import { useDocumentTitle } from '@/shared/lib/document-title';

const people = (list: ScheduledItem['to']) =>
  list.map((a) => (a.name ? `${a.name} <${a.address}>` : a.address)).join(', ');

export interface ScheduledDetailProps {
  /** The message that is open; none shows a hint. */
  id?: string;
}

/**
 * The card of a scheduled message in the reading area (DESIGN.md §5, "Отложенные"): not a message of the mailbox, so there is no text, only what
 * is known about it and what can be done: cancel the sending (it goes back among the drafts), or, for one that did not go, read why and open the
 * drafts or remove the note.
 */
export function ScheduledDetail({ id }: ScheduledDetailProps) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const toast = useToast();
  const mobile = useLayout() === 'mobile';
  const query = useScheduled();
  const cancel = useCancelScheduled();
  const item = id === undefined ? undefined : query.data?.find((i) => i.id === id);
  // the card names the tab with its subject (the list under it names it "Scheduled")
  useDocumentTitle(item ? item.subject || t('list.noSubject') : null, 1);

  if (id === undefined) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-3 p-8 text-center text-muted">
        <MailOpen size={40} strokeWidth={1.4} aria-hidden />
        <p>{t('scheduled.noneOpen')}</p>
      </div>
    );
  }

  const back = mobile && (
    <IconButton
      aria-label={t('mailbox.back')}
      onClick={() => void navigate(scheduledPath())}
      className="-ml-1"
    >
      <ArrowLeft size={18} strokeWidth={1.8} aria-hidden />
    </IconButton>
  );

  if (!item) {
    return (
      <div className="flex h-full flex-col gap-3 px-10 py-7 max-md:px-4">
        {back}
        {query.isPending ? (
          <p role="status" className="text-muted">
            {t('common.loading')}
          </p>
        ) : (
          <p role="alert" className="text-text-2">
            {t('scheduled.gone')}
          </p>
        )}
      </div>
    );
  }

  const failed = item.state === 'failed';
  const run = () =>
    cancel.mutate(item.id, {
      onSuccess: () => {
        toast.show({ message: t(failed ? 'scheduled.noteRemoved' : 'compose.schedule.cancelled') });
        void navigate(scheduledPath());
      },
    });

  return (
    <article aria-labelledby="scheduled-subject" className="flex h-full flex-col overflow-y-auto">
      <div className="mx-auto flex w-full max-w-[760px] flex-col gap-5 px-10 py-7 max-md:px-4">
        {back}
        <h1 id="scheduled-subject" className="text-subject font-bold text-ink">
          {item.subject || t('list.noSubject')}
        </h1>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-base">
          <dt className="text-muted">{t('compose.to')}</dt>
          <dd className="min-w-0 break-words text-ink">{people(item.to) || t('list.noSender')}</dd>
          {item.cc.length > 0 && (
            <>
              <dt className="text-muted">{t('compose.cc')}</dt>
              <dd className="min-w-0 break-words text-ink">{people(item.cc)}</dd>
            </>
          )}
          <dt className="text-muted">
            {t(failed ? 'scheduled.shouldHaveGone' : 'scheduled.sendsAt')}
          </dt>
          <dd className="text-ink">
            <time dateTime={item.sendAt}>{fullWhen(new Date(item.sendAt), i18n.language)}</time>
          </dd>
        </dl>
        {failed && (
          <section
            aria-label={t('scheduled.failedTitle')}
            className="flex flex-col gap-1 rounded-md bg-warning-bg px-4 py-3 text-base text-warning"
          >
            <p className="font-semibold">{problemText(item.problem, t)}</p>
            <p>{t(item.inDrafts ? 'scheduled.inDrafts' : 'scheduled.willBeInDrafts')}</p>
          </section>
        )}
        <div className="flex flex-wrap gap-2">
          {failed ? (
            <>
              <Link
                to="/mail/drafts"
                className="inline-flex h-9 items-center rounded-xs border border-control bg-surface px-3 text-base font-semibold text-text-2 transition-colors hover:bg-bg max-md:h-11"
              >
                {t('scheduled.openDrafts')}
              </Link>
              <Button
                variant="ghost"
                size="sm"
                className="max-md:h-11"
                disabled={cancel.isPending}
                onClick={run}
              >
                {t('scheduled.removeNote')}
              </Button>
            </>
          ) : (
            <Button
              variant="secondary"
              size="sm"
              className="max-md:h-11"
              disabled={cancel.isPending}
              onClick={run}
            >
              {t('scheduled.cancel')}
            </Button>
          )}
        </div>
        {cancel.isError && (
          <p role="alert" className="text-secondary text-warning">
            {describeApiError(cancel.error, t)}
          </p>
        )}
      </div>
    </article>
  );
}
