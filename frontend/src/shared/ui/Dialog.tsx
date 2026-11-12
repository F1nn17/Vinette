import { X } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { cn } from '../lib/cn';

export interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  /** Required: the cross has no other accessible name. */
  closeLabel: string;
  children: ReactNode;
  className?: string;
}

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * A window over the screen (DESIGN.md section 12): a card in the middle, the screen dimmed behind it. The focus goes into
 * it and stays there (Tab goes round), Escape, the cross and a click on the dimmed part close it, and the focus goes back
 * to where it was. It takes almost the whole screen on a phone.
 */
export function Dialog({ open, onClose, title, closeLabel, children, className }: DialogProps) {
  const titleId = useId();
  const card = useRef<HTMLDivElement>(null);
  const returnTo = useRef<Element | null>(null);

  useEffect(() => {
    if (!open) return;
    returnTo.current = document.activeElement;
    card.current?.querySelector<HTMLElement>(FOCUSABLE)?.focus();

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        onClose();
        return;
      }
      if (event.key !== 'Tab' || !card.current) return;
      const items = [...card.current.querySelectorAll<HTMLElement>(FOCUSABLE)];
      const first = items[0];
      const last = items.at(-1);
      if (!first || !last) return;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      } else if (!card.current.contains(document.activeElement)) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      if (returnTo.current instanceof HTMLElement) returnTo.current.focus();
    };
  }, [open, onClose]);

  if (!open) return null;
  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-ink/40 p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div
        ref={card}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className={cn(
          'flex max-h-full w-full max-w-[480px] flex-col rounded-2xl bg-surface p-6 shadow-card max-md:p-5',
          className,
        )}
      >
        <div className="mb-4 flex items-center gap-2">
          <h2 id={titleId} className="min-w-0 flex-1 text-lead font-bold text-ink">
            {title}
          </h2>
          <button
            type="button"
            aria-label={closeLabel}
            onClick={onClose}
            className="-mr-2 inline-flex size-10 shrink-0 items-center justify-center rounded-sm text-text-2 hover:bg-bg focus-visible:outline-2 focus-visible:outline-accent max-md:size-11"
          >
            <X size={18} strokeWidth={1.8} aria-hidden />
          </button>
        </div>
        <div className="min-h-0 overflow-y-auto">{children}</div>
      </div>
    </div>,
    document.body,
  );
}
