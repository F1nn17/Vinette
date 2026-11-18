import { Paperclip } from 'lucide-react';
import { useRef } from 'react';
import { useTranslation } from 'react-i18next';

export interface AttachButtonProps {
  onFiles: (files: File[]) => void;
}

/** "Attach file" (the paperclip of the footer, DESIGN.md §6): opens the file chooser; several files may be chosen at once. */
export function AttachButton({ onFiles }: AttachButtonProps) {
  const { t } = useTranslation();
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <button
        type="button"
        aria-label={t('compose.attach')}
        title={t('compose.attach')}
        onClick={() => input.current?.click()}
        className="inline-flex size-10 items-center justify-center rounded-sm text-text-2 transition-colors hover:bg-bg max-md:size-11"
      >
        <Paperclip size={18} strokeWidth={1.8} aria-hidden />
      </button>
      <input
        ref={input}
        type="file"
        multiple
        tabIndex={-1}
        aria-hidden="true"
        data-testid="attach-input"
        className="hidden"
        onChange={(e) => {
          const files = [...(e.target.files ?? [])];
          e.target.value = ''; // the same file may be chosen again after it was taken off
          if (files.length > 0) onFiles(files);
        }}
      />
    </>
  );
}
