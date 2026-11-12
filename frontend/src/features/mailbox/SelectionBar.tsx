import { AlertCircle, Archive, ListChecks, MailOpen, Trash2, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '@/shared/ui';
import type { BatchAction } from './api';
import { MAX_SELECTED } from './selection';

export interface SelectionBarProps {
  /** How many messages are selected. */
  count: number;
  /** Every loaded message (up to the limit) is selected. */
  allSelected: boolean;
  /** The role of the folder the list shows: the action that would leave the messages where they are is not offered. */
  folderRole?: string;
  /** An action is running: the buttons wait. */
  busy: boolean;
  /** A selection went over the limit: say so. */
  capped: boolean;
  /** What went wrong with the last action, in plain words. */
  error?: string | null;
  onSelectAll: () => void;
  onClear: () => void;
  onAction: (action: BatchAction) => void;
  /** A phone: "select all" is an icon, so that everything fits in one line. */
  narrow?: boolean;
}

/**
 * Takes the place of the list's heading while messages are selected (VIN-073, approved by the owner): how many are selected,
 * "select all", the actions (archive, spam, delete, mark as read) with the icons of the reading pane, and "clear".
 */
export function SelectionBar({
  count,
  allSelected,
  folderRole,
  busy,
  capped,
  error,
  onSelectAll,
  onClear,
  onAction,
  narrow = false,
}: SelectionBarProps) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2">
      <div role="toolbar" aria-label={t('selection.actions')} className="flex items-center gap-1">
        <IconButton aria-label={t('selection.clear')} onClick={onClear} className="-ml-2">
          <X size={18} strokeWidth={1.8} aria-hidden />
        </IconButton>
        <span aria-live="polite" className="shrink-0 font-semibold whitespace-nowrap text-ink">
          {t('selection.count', { count })}
        </span>
        <span className="min-w-0 flex-1" />
        {!allSelected &&
          (narrow ? (
            <IconButton aria-label={t('selection.selectAll')} onClick={onSelectAll}>
              <ListChecks size={18} strokeWidth={1.8} aria-hidden />
            </IconButton>
          ) : (
            <button
              type="button"
              onClick={onSelectAll}
              className="min-h-10 shrink-0 rounded-sm px-2 text-secondary font-medium text-accent hover:bg-bg focus-visible:outline-2 focus-visible:outline-accent"
            >
              {t('selection.selectAll')}
            </button>
          ))}
        {folderRole !== 'archive' && (
          <IconButton
            aria-label={t('selection.archive')}
            disabled={busy}
            onClick={() => onAction('archive')}
          >
            <Archive size={18} strokeWidth={1.8} aria-hidden />
          </IconButton>
        )}
        {folderRole !== 'junk' && (
          <IconButton
            aria-label={t('selection.spam')}
            disabled={busy}
            onClick={() => onAction('spam')}
          >
            <AlertCircle size={18} strokeWidth={1.8} aria-hidden />
          </IconButton>
        )}
        {folderRole !== 'trash' && (
          <IconButton
            aria-label={t('selection.trash')}
            disabled={busy}
            onClick={() => onAction('trash')}
          >
            <Trash2 size={18} strokeWidth={1.8} aria-hidden />
          </IconButton>
        )}
        <IconButton
          aria-label={t('selection.read')}
          disabled={busy}
          onClick={() => onAction('read')}
        >
          <MailOpen size={18} strokeWidth={1.8} aria-hidden />
        </IconButton>
      </div>
      {capped && (
        <p role="status" className="text-secondary text-muted">
          {t('selection.limit', { max: MAX_SELECTED })}
        </p>
      )}
      {error && (
        <p role="alert" className="text-secondary text-warning">
          {error}
        </p>
      )}
    </div>
  );
}
