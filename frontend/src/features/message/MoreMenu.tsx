import { Ellipsis } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '@/shared/ui';
import { cn } from '@/shared/lib';

export interface MoreMenuItem {
  key: string;
  label: string;
  icon: ReactNode;
  onSelect: () => void;
  disabled?: boolean;
  /** Shown only where the action bar has no button of its own for it (a phone). */
  phoneOnly?: boolean;
}

/**
 * "More" (DESIGN.md §5): a panel of the actions that have no room in the action bar. On a phone the bar shows no action
 * buttons, so the panel carries them all. It closes on Escape, on a click outside it and when an action is chosen.
 */
export function MoreMenu({ items }: { items: MoreMenuItem[] }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const panelId = useId();

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    const onPointer = (event: PointerEvent) => {
      if (root.current && !root.current.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('pointerdown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('pointerdown', onPointer);
    };
  }, [open]);

  return (
    <div ref={root} className="relative">
      <IconButton
        aria-label={t('reader.more')}
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((value) => !value)}
      >
        <Ellipsis size={18} strokeWidth={1.8} aria-hidden />
      </IconButton>
      {open && (
        <div
          id={panelId}
          role="group"
          aria-label={t('reader.more')}
          className="absolute top-full right-0 z-10 mt-1 flex w-72 flex-col rounded-md border border-line bg-surface p-1 shadow-card"
        >
          {items.map((item) => (
            <button
              key={item.key}
              type="button"
              disabled={item.disabled}
              onClick={() => {
                setOpen(false);
                item.onSelect();
              }}
              className={cn(
                'flex min-h-11 items-center gap-3 rounded-sm px-3 text-left text-base text-ink hover:bg-bg focus-visible:outline-2 focus-visible:outline-accent disabled:cursor-not-allowed disabled:text-muted md:min-h-10',
                item.phoneOnly && 'md:hidden',
              )}
            >
              <span aria-hidden="true" className="shrink-0 text-text-2">
                {item.icon}
              </span>
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
