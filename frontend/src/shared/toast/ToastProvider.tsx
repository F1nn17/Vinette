import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Toast } from '../ui/Toast';
import {
  MAX_TOASTS,
  TOAST_DURATION_MS,
  TOAST_ERROR_DURATION_MS,
  ToastContext,
  type ToastApi,
  type ToastOptions,
} from './toast-context';

interface Shown extends ToastOptions {
  id: number;
}

/** One toast with its own timer: it runs out after its time and waits while the pointer or the focus is on it. */
function Timed({ toast, onClose }: { toast: Shown; onClose: () => void }) {
  const { t } = useTranslation();
  const [paused, setPaused] = useState(false);
  const duration =
    toast.duration ?? (toast.tone === 'error' ? TOAST_ERROR_DURATION_MS : TOAST_DURATION_MS);

  useEffect(() => {
    if (paused || duration <= 0) return;
    const timer = setTimeout(onClose, duration);
    return () => clearTimeout(timer);
  }, [paused, duration, onClose]);

  return (
    <div
      onPointerEnter={() => setPaused(true)}
      onPointerLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
    >
      <Toast
        tone={toast.tone}
        closeLabel={t('common.close')}
        onClose={onClose}
        actionLabel={toast.action?.label}
        onAction={
          toast.action
            ? () => {
                onClose();
                toast.action?.onSelect();
              }
            : undefined
        }
      >
        {toast.message}
      </Toast>
    </div>
  );
}

/**
 * Shows toasts at the bottom of the screen, in the middle (DESIGN.md section 12); on a phone above the "Compose" button.
 * The newest is the lowest. They are not part of the page flow, so a screen reader hears them as they come (role
 * alert/status).
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Shown[]>([]);
  const next = useRef(1);

  const dismiss = useCallback(
    (id: number) => setToasts((all) => all.filter((t) => t.id !== id)),
    [],
  );
  const show = useCallback((options: ToastOptions) => {
    const id = next.current++;
    setToasts((all) => [...all, { ...options, id }].slice(-MAX_TOASTS));
    return id;
  }, []);
  const api = useMemo<ToastApi>(() => ({ show, dismiss }), [show, dismiss]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      {toasts.length > 0 && (
        <div className="pointer-events-none fixed inset-x-0 bottom-6 z-50 flex flex-col items-center gap-2 px-4 max-md:bottom-24">
          {toasts.map((toast) => (
            <div key={toast.id} className="pointer-events-auto max-w-full">
              <Timed toast={toast} onClose={() => dismiss(toast.id)} />
            </div>
          ))}
        </div>
      )}
    </ToastContext.Provider>
  );
}
