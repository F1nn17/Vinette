import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@/shared/ui';
import { cn } from '@/shared/lib/cn';
import {
  formatWhen,
  fromLocalInput,
  isLaterEnough,
  MIN_LATER_MINUTES,
  presetsFrom,
  toLocalInput,
} from './schedule';

export interface ScheduleDialogProps {
  open: boolean;
  onClose: () => void;
  /** Puts the message off to this time. */
  onSchedule: (at: Date) => void;
  /** The message is being put in the queue. */
  busy?: boolean;
}

/**
 * "Send later" (the chevron of the send button, DESIGN.md §6): a few ready times and a field for any other. The chosen time is written out in
 * words before it is confirmed, so that nobody has to read a field to learn when the message will go.
 */
export function ScheduleDialog({ open, onClose, onSchedule, busy = false }: ScheduleDialogProps) {
  const { t, i18n } = useTranslation();
  // The ready times and the earliest are taken when the window opens; they are not recomputed while it is open.
  const [now] = useState(() => new Date());
  const presets = presetsFrom(now);
  const [value, setValue] = useState(() => toLocalInput(presets[1]?.at ?? now));
  const at = fromLocalInput(value);
  const fine = at !== null && isLaterEnough(at, new Date());

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t('compose.schedule.title')}
      closeLabel={t('common.close')}
    >
      <div className="flex flex-col gap-4">
        <ul aria-label={t('compose.schedule.ready')} className="flex flex-wrap gap-2">
          {presets.map((p) => (
            <li key={p.key}>
              <button
                type="button"
                aria-pressed={at?.getTime() === p.at.getTime()}
                onClick={() => setValue(toLocalInput(p.at))}
                className={cn(
                  'inline-flex h-9 items-center rounded-pill border px-3.5 text-secondary font-medium max-md:h-11',
                  at?.getTime() === p.at.getTime()
                    ? 'border-accent bg-accent-soft text-accent'
                    : 'border-control bg-surface text-text-2 hover:bg-bg',
                )}
              >
                {t(`compose.schedule.${p.key}`)}
              </button>
            </li>
          ))}
        </ul>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="schedule-when" className="text-secondary font-medium text-text-2">
            {t('compose.schedule.custom')}
          </label>
          <input
            id="schedule-when"
            type="datetime-local"
            value={value}
            min={toLocalInput(new Date(now.getTime() + MIN_LATER_MINUTES * 60_000))}
            onChange={(e) => setValue(e.target.value)}
            aria-invalid={at !== null && !fine ? true : undefined}
            className="h-11 w-full rounded-sm border border-field bg-surface px-3 text-base text-ink focus:border-accent focus:outline-2 focus:outline-accent aria-invalid:border-warning"
          />
        </div>
        {at && fine && (
          <p role="status" className="text-base text-ink">
            {t('compose.schedule.willSend', { when: formatWhen(at, i18n.language) })}
          </p>
        )}
        {at !== null && !fine && (
          <p role="alert" className="text-secondary text-warning">
            {t('compose.schedule.tooSoon')}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button disabled={!fine || busy} onClick={() => at && onSchedule(at)}>
            {t(busy ? 'compose.sending' : 'compose.schedule.confirm')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
