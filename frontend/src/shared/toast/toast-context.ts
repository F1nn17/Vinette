import { createContext, useContext, type ReactNode } from 'react';

export interface ToastOptions {
  message: ReactNode;
  /** `error` is announced at once; the default is an ordinary message. */
  tone?: 'info' | 'error';
  /** An action next to the message (for example "Undo"); the toast closes when it is chosen. */
  action?: { label: string; onSelect: () => void };
  /** How long it stays, in milliseconds; an error stays longer by default. 0 keeps it until it is closed. */
  duration?: number;
}

export interface ToastApi {
  /** Shows a toast and returns its id. At most `MAX_TOASTS` are shown: the oldest goes first. */
  show: (options: ToastOptions) => number;
  dismiss: (id: number) => void;
}

/** How long a toast stays by default (DESIGN.md section 12). */
export const TOAST_DURATION_MS = 6000;
export const TOAST_ERROR_DURATION_MS = 8000;
export const MAX_TOASTS = 3;

export const ToastContext = createContext<ToastApi | null>(null);

export function useToast(): ToastApi {
  const value = useContext(ToastContext);
  if (!value) throw new Error('useToast must be used inside <ToastProvider>');
  return value;
}
