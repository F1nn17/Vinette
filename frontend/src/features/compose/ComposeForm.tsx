import { X } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/shared/lib/cn';
import type { ComposeValues } from './compose-values';
import { RecipientField } from './RecipientField';

export interface ComposeFormProps {
  /** The mailbox the message goes from. */
  address: string;
  values: ComposeValues;
  onChange: (values: ComposeValues) => void;
  /** More of the screen under the fields: the editor, attachments and the footer. */
  children?: ReactNode;
}

const rowClass =
  'flex min-h-[52px] items-center gap-3 border-b border-subtle px-5 focus-within:outline-2 focus-within:-outline-offset-2 focus-within:outline-accent';
const labelClass = 'w-14 shrink-0 text-secondary text-muted';
const inputClass =
  'min-w-0 flex-1 bg-transparent py-3 text-base text-ink placeholder:text-muted focus:outline-none';
const toggleClass =
  'relative inline-flex h-9 items-center rounded-xs px-2 text-secondary text-text-2 transition-colors hover:bg-bg max-md:h-11';

/**
 * The fields of a new message (DESIGN.md §6): "From", "To" with the "Cc" and "Bcc" rows that open on demand, and the subject. The "From" row
 * offers the signed-in mailbox only: a mailbox has no other sending addresses yet.
 */
export function ComposeForm({ address, values, onChange, children }: ComposeFormProps) {
  const { t } = useTranslation();
  const ids = { from: useId(), to: useId(), cc: useId(), bcc: useId(), subject: useId() };
  // A row stays open when it holds something: closing it would hide recipients the user cannot see anymore.
  const [ccOpen, setCcOpen] = useState(values.cc.length > 0);
  const [bccOpen, setBccOpen] = useState(values.bcc.length > 0);
  const set = (patch: Partial<ComposeValues>) => onChange({ ...values, ...patch });

  const extraRow = (key: 'cc' | 'bcc', close: () => void, removeLabel: string): ReactNode => (
    <div className={rowClass}>
      <label htmlFor={ids[key]} className={labelClass}>
        {t(`compose.${key}`)}
      </label>
      <RecipientField
        id={ids[key]}
        label={t(`compose.${key}`)}
        autoFocus
        value={values[key]}
        onChange={(v) => set({ [key]: v })}
      />
      {values[key].length === 0 && (
        <button
          type="button"
          aria-label={removeLabel}
          onClick={close}
          className="inline-flex size-9 shrink-0 items-center justify-center rounded-xs text-text-2 hover:bg-bg max-md:size-11"
        >
          <X size={18} strokeWidth={1.8} aria-hidden />
        </button>
      )}
    </div>
  );

  return (
    <form
      aria-label={t('compose.form')}
      onSubmit={(e) => e.preventDefault()}
      className="flex min-w-0 flex-col rounded-xl border border-line bg-surface"
    >
      <div className={rowClass}>
        <label htmlFor={ids.from} className={labelClass}>
          {t('compose.from')}
        </label>
        <select id={ids.from} defaultValue={address} className={cn(inputClass, 'appearance-none')}>
          <option value={address}>{address}</option>
        </select>
      </div>

      <div className={rowClass}>
        <label htmlFor={ids.to} className={labelClass}>
          {t('compose.to')}
        </label>
        <RecipientField
          id={ids.to}
          label={t('compose.to')}
          value={values.to}
          onChange={(v) => set({ to: v })}
        />
        {!ccOpen && (
          <button
            type="button"
            aria-label={t('compose.addCc')}
            onClick={() => setCcOpen(true)}
            className={toggleClass}
          >
            {t('compose.cc')}
          </button>
        )}
        {!bccOpen && (
          <button
            type="button"
            aria-label={t('compose.addBcc')}
            onClick={() => setBccOpen(true)}
            className={toggleClass}
          >
            {t('compose.bcc')}
          </button>
        )}
      </div>

      {ccOpen && extraRow('cc', () => setCcOpen(false), t('compose.removeCc'))}
      {bccOpen && extraRow('bcc', () => setBccOpen(false), t('compose.removeBcc'))}

      <div className={rowClass}>
        <label htmlFor={ids.subject} className={labelClass}>
          {t('compose.subject')}
        </label>
        <input
          id={ids.subject}
          type="text"
          autoComplete="off"
          value={values.subject}
          onChange={(e) => set({ subject: e.target.value })}
          className={cn(inputClass, 'text-message font-semibold')}
        />
      </div>

      {children}
    </form>
  );
}
