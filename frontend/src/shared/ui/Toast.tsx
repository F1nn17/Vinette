import { AlertCircle, X } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

export interface ToastProps {
  /** `error` is announced at once (role "alert") and has the warning icon; `info` is announced politely (role "status"). */
  tone?: 'info' | 'error';
  children: ReactNode;
  /** An action next to the message ("Undo"); both parts are needed. */
  actionLabel?: string;
  onAction?: () => void;
  /** Required: the cross has no other accessible name. */
  closeLabel: string;
  onClose: () => void;
  className?: string;
}

/**
 * A short message that does not block the screen (DESIGN.md §12): a dark bar with the text, an optional action and a
 * cross. Placing it and removing it after a while is the job of the toast provider (`shared/toast`).
 */
export function Toast({
  tone = 'info',
  children,
  actionLabel,
  onAction,
  closeLabel,
  onClose,
  className,
}: ToastProps) {
  return (
    <div
      role={tone === 'error' ? 'alert' : 'status'}
      data-tone={tone}
      className={cn(
        'flex w-max max-w-full items-center gap-3 rounded-md bg-ink py-2 pr-2 pl-4 text-base text-surface shadow-card',
        className,
      )}
    >
      {tone === 'error' && (
        <AlertCircle size={18} strokeWidth={1.8} aria-hidden className="shrink-0" />
      )}
      <p className="min-w-0 flex-1 py-1">{children}</p>
      {actionLabel && onAction && (
        <button
          type="button"
          onClick={onAction}
          className="min-h-10 shrink-0 rounded-xs px-2 font-semibold underline underline-offset-2 hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-surface max-md:min-h-11"
        >
          {actionLabel}
        </button>
      )}
      <button
        type="button"
        aria-label={closeLabel}
        onClick={onClose}
        className="inline-flex size-10 shrink-0 items-center justify-center rounded-xs hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-surface max-md:size-11"
      >
        <X size={18} strokeWidth={1.8} aria-hidden />
      </button>
    </div>
  );
}
