import { File, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { describeApiError } from '@/shared/api/describe-error';
import { formatBytes } from '@/shared/lib';
import { cn } from '@/shared/lib/cn';
import { ProgressBar } from '@/shared/ui';
import { extensionOf } from '@/features/message';
import type { AttachmentItem } from './attachments';

export interface AttachmentCardsProps {
  items: AttachmentItem[];
  /** Takes a file off the message; for a file still being sent it cancels the upload. */
  onRemove: (key: string) => void;
}

/**
 * The files of the message (DESIGN.md §6): a card for each with the file type, the name and the size. A file being sent shows its
 * percentage and a progress bar in the accent colour and can be cancelled; a file that could not be attached says why in words and can
 * be taken off. Every card has a remove button named after its file.
 */
export function AttachmentCards({ items, onRemove }: AttachmentCardsProps) {
  const { t, i18n } = useTranslation();
  if (items.length === 0) return null;

  return (
    <ul
      aria-label={t('compose.files')}
      className="flex flex-wrap gap-3 border-t border-subtle px-5 py-3"
    >
      {items.map((item) => {
        const size = formatBytes(item.size, i18n.language);
        const extension = extensionOf(item.fileName);
        const percent = Math.round(item.progress * 100);
        const failed = item.status === 'failed';
        const uploading = item.status === 'uploading';
        return (
          <li
            key={item.key}
            className={cn(
              'flex min-h-[52px] w-72 max-w-full items-center gap-3 rounded-md border bg-surface px-3 py-2',
              failed ? 'border-warning' : 'border-control',
            )}
          >
            <span
              aria-hidden="true"
              className="flex size-[38px] shrink-0 items-center justify-center rounded-sm bg-bg text-caps font-bold text-text-2"
            >
              {extension ?? <File size={18} strokeWidth={1.8} />}
            </span>
            <span className="min-w-0 flex-1">
              <span className="flex items-baseline justify-between gap-2">
                <span className="truncate text-base text-ink">{item.fileName}</span>
                {uploading && (
                  <span className="shrink-0 text-meta text-muted" aria-hidden="true">
                    {percent}%
                  </span>
                )}
              </span>
              {uploading ? (
                <ProgressBar
                  aria-label={t('compose.uploading', { name: item.fileName })}
                  value={percent}
                  className="mt-1.5"
                />
              ) : failed ? (
                <span role="alert" className="block text-meta text-warning">
                  {describeApiError(item.error, t)}
                </span>
              ) : (
                <span className="block text-meta text-muted">{size}</span>
              )}
            </span>
            <button
              type="button"
              aria-label={t(uploading ? 'compose.cancelUpload' : 'compose.removeFile', {
                name: item.fileName,
              })}
              onClick={() => onRemove(item.key)}
              className="relative inline-flex size-8 shrink-0 items-center justify-center rounded-sm text-text-2 hover:bg-bg max-md:after:absolute max-md:after:-inset-1.5 max-md:after:content-['']"
            >
              <X size={18} strokeWidth={1.8} aria-hidden />
            </button>
          </li>
        );
      })}
    </ul>
  );
}
