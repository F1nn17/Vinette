import {
  AlertCircle,
  Archive,
  ArrowLeft,
  Ellipsis,
  Flag,
  Forward,
  Reply,
  Trash2,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '@/shared/ui';

export interface ReaderActionBarProps {
  /** The message is marked with a flag. */
  flagged: boolean;
  /** Sets or removes the flag; without it the flag button cannot be used. */
  onToggleFlag?: () => void;
  /** On a phone the message fills the screen: this goes back to the list. */
  onBack?: () => void;
}

/**
 * The actions of an open message (DESIGN.md §5). The flag works; the others are in place as in the design but cannot be
 * used yet: archiving, spam and deleting arrive with the actions (VIN-071, VIN-074) and replying with the editor
 * (VIN-102), and each of those switches its own button on.
 */
export function ReaderActionBar({ flagged, onToggleFlag, onBack }: ReaderActionBarProps) {
  const { t } = useTranslation();
  const icon = { size: 18, strokeWidth: 1.8, 'aria-hidden': true } as const;
  return (
    <div
      role="toolbar"
      aria-label={t('reader.actions')}
      className="flex shrink-0 items-center gap-2 border-b border-subtle px-7 py-3 max-md:px-3"
    >
      {onBack && (
        <IconButton aria-label={t('mailbox.back')} onClick={onBack} className="-ml-1">
          <ArrowLeft {...icon} />
        </IconButton>
      )}
      <Button
        variant="secondary"
        size="sm"
        className="max-md:h-11"
        icon={<Reply {...icon} size={16} />}
        disabled
      >
        {t('reader.reply')}
      </Button>
      <Button
        variant="secondary"
        size="sm"
        className="max-md:h-11"
        icon={<Forward {...icon} size={16} />}
        disabled
      >
        {t('reader.forward')}
      </Button>
      <span aria-hidden="true" className="mx-1 h-5 w-px bg-line max-md:hidden" />
      <div className="flex items-center max-md:hidden">
        <IconButton aria-label={t('reader.archive')} disabled>
          <Archive {...icon} />
        </IconButton>
        <IconButton aria-label={t('reader.spam')} disabled>
          <AlertCircle {...icon} />
        </IconButton>
        <IconButton aria-label={t('reader.delete')} disabled>
          <Trash2 {...icon} />
        </IconButton>
        <IconButton
          aria-label={t('reader.flag')}
          pressed={flagged}
          disabled={onToggleFlag === undefined}
          onClick={onToggleFlag}
        >
          <Flag {...icon} className={flagged ? 'fill-flag' : undefined} />
        </IconButton>
      </div>
      <div className="ml-auto">
        <IconButton aria-label={t('reader.more')} disabled>
          <Ellipsis {...icon} />
        </IconButton>
      </div>
    </div>
  );
}
