import { SlidersHorizontal } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Chip, IconButton } from '@/shared/ui';
import type { ListDensity } from '@/features/settings';

export interface ListOptionsMenuProps {
  density: ListDensity;
  onDensityChange: (density: ListDensity) => void;
}

const DENSITIES: ListDensity[] = ['comfortable', 'compact'];

/**
 * "List options" (DESIGN.md §5): a button that opens a small panel with the density of the list. The panel closes on
 * Escape, on a click outside it and when the choice is made.
 */
export function ListOptionsMenu({ density, onDensityChange }: ListOptionsMenuProps) {
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
        aria-label={t('list.options')}
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((value) => !value)}
      >
        <SlidersHorizontal size={18} strokeWidth={1.8} aria-hidden />
      </IconButton>
      {open && (
        <div
          id={panelId}
          role="group"
          aria-label={t('list.density')}
          className="absolute top-full right-0 z-10 mt-1 flex w-60 flex-col gap-2 rounded-md border border-line bg-surface p-3 shadow-card"
        >
          <p className="text-caps font-semibold tracking-wider text-muted uppercase">
            {t('list.density')}
          </p>
          <div className="flex flex-wrap gap-2">
            {DENSITIES.map((value) => (
              <Chip
                key={value}
                pressed={density === value}
                onClick={() => {
                  onDensityChange(value);
                  setOpen(false);
                }}
              >
                {t(`list.densityOption.${value}`)}
              </Chip>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
