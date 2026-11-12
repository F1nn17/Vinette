import {
  AlertCircle,
  Archive,
  ArrowLeft,
  Flag,
  Forward,
  MailPlus,
  Reply,
  Trash2,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '@/shared/ui';
import { MoreMenu } from './MoreMenu';

/** What the buttons that move or mark the open message ask for. */
export type ReaderAction = 'archive' | 'spam' | 'trash' | 'unread';

export interface ReaderActionBarProps {
  /** The message is marked with a flag. */
  flagged: boolean;
  /** Sets or removes the flag; without it the flag button cannot be used. */
  onToggleFlag?: () => void;
  /** On a phone the message fills the screen: this goes back to the list. */
  onBack?: () => void;
  /** The kind of folder the message is in: the action that would leave it where it is is not offered. */
  folderRole?: string;
  /** Archives, marks as spam, deletes (moves to the trash) or marks as unread; without it those buttons cannot be used. */
  onAction?: (action: ReaderAction) => void;
}

/**
 * The actions of an open message (DESIGN.md §5). Archive, spam, delete (to the trash), the flag and "More" work (VIN-074);
 * replying and forwarding are in place as in the design and arrive with the editor (VIN-102). On a phone the bar has no
 * action buttons: "More" carries them.
 */
export function ReaderActionBar({
  flagged,
  onToggleFlag,
  onBack,
  folderRole,
  onAction,
}: ReaderActionBarProps) {
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
        {folderRole !== 'archive' && (
          <IconButton
            aria-label={t('reader.archive')}
            disabled={!onAction}
            onClick={() => onAction?.('archive')}
          >
            <Archive {...icon} />
          </IconButton>
        )}
        {folderRole !== 'junk' && (
          <IconButton
            aria-label={t('reader.spam')}
            disabled={!onAction}
            onClick={() => onAction?.('spam')}
          >
            <AlertCircle {...icon} />
          </IconButton>
        )}
        {folderRole !== 'trash' && (
          <IconButton
            aria-label={t('reader.delete')}
            disabled={!onAction}
            onClick={() => onAction?.('trash')}
          >
            <Trash2 {...icon} />
          </IconButton>
        )}
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
        <MoreMenu
          items={[
            ...(folderRole !== 'archive'
              ? [
                  {
                    key: 'archive',
                    label: t('reader.archive'),
                    icon: <Archive {...icon} />,
                    onSelect: () => onAction?.('archive'),
                    disabled: !onAction,
                    phoneOnly: true,
                  },
                ]
              : []),
            ...(folderRole !== 'junk'
              ? [
                  {
                    key: 'spam',
                    label: t('reader.spam'),
                    icon: <AlertCircle {...icon} />,
                    onSelect: () => onAction?.('spam'),
                    disabled: !onAction,
                    phoneOnly: true,
                  },
                ]
              : []),
            ...(folderRole !== 'trash'
              ? [
                  {
                    key: 'trash',
                    label: t('reader.delete'),
                    icon: <Trash2 {...icon} />,
                    onSelect: () => onAction?.('trash'),
                    disabled: !onAction,
                    phoneOnly: true,
                  },
                ]
              : []),
            {
              key: 'flag',
              label: t('reader.flag'),
              icon: <Flag {...icon} className={flagged ? 'fill-flag' : undefined} />,
              onSelect: () => onToggleFlag?.(),
              disabled: onToggleFlag === undefined,
              phoneOnly: true,
            },
            {
              key: 'unread',
              label: t('reader.markUnread'),
              icon: <MailPlus {...icon} />,
              onSelect: () => onAction?.('unread'),
              disabled: !onAction,
            },
          ]}
        />
      </div>
    </div>
  );
}
