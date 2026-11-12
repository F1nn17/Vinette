import { Download, File } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '@/shared/lib';
import type { MessageAttachment } from './api';
import { attachmentHref, extensionOf } from './attachment-helpers';

export interface AttachmentListProps {
  messageId: string;
  attachments: MessageAttachment[];
}

/**
 * "Attachments · N" with a card for each file (DESIGN.md §5): a tile with the file type, the name and the size, and the
 * download icon. A card is one link, so it is reached with Tab and opened with Enter; it downloads the file, it never
 * shows it in the browser.
 */
export function AttachmentList({ messageId, attachments }: AttachmentListProps) {
  const { t, i18n } = useTranslation();
  const headingId = useId();
  if (attachments.length === 0) return null;

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-3">
      <h2 id={headingId} className="text-secondary font-semibold text-ink">
        {t('reader.attachments', { count: attachments.length })}
      </h2>
      <ul className="grid grid-cols-2 gap-3 max-md:grid-cols-1">
        {attachments.map((attachment) => {
          const size = formatBytes(Number(attachment.size), i18n.language);
          const extension = extensionOf(attachment.fileName);
          return (
            <li key={attachment.partId}>
              <a
                href={attachmentHref(messageId, attachment.partId)}
                download={attachment.fileName}
                aria-label={t('reader.download', { name: attachment.fileName, size })}
                className="flex min-h-14 items-center gap-3 rounded-md border border-line bg-surface px-3 py-2.5 transition-colors hover:bg-bg"
              >
                <span
                  aria-hidden="true"
                  className="flex size-[38px] shrink-0 items-center justify-center rounded-sm bg-bg text-caps font-bold text-text-2"
                >
                  {extension ?? <File size={18} strokeWidth={1.8} />}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-base text-ink">{attachment.fileName}</span>
                  <span className="block text-meta text-muted">{size}</span>
                </span>
                <Download size={18} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
              </a>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
