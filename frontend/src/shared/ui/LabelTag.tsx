import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

export type LabelColor = 'work' | 'bills' | 'personal';

const colors: Record<LabelColor, { tag: string; dot: string }> = {
  work: { tag: 'bg-label-work-bg text-label-work', dot: 'bg-label-work-dot' },
  bills: { tag: 'bg-label-bills-bg text-label-bills', dot: 'bg-label-bills-dot' },
  personal: { tag: 'bg-label-personal-bg text-label-personal', dot: 'bg-label-personal-dot' },
};

export function LabelTag({
  color,
  children,
  className,
}: {
  color: LabelColor;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      data-color={color}
      className={cn(
        'inline-block rounded-pill px-2 py-0.5 text-caps font-medium',
        colors[color].tag,
        className,
      )}
    >
      {children}
    </span>
  );
}

/** Colored square dot used in the sidebar label list. */
export function LabelDot({ color }: { color: LabelColor }) {
  return (
    <span
      aria-hidden="true"
      className={cn('inline-block size-2.5 rounded-[3px]', colors[color].dot)}
    />
  );
}
