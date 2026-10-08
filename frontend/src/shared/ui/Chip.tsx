import type { ButtonHTMLAttributes } from 'react';
import { cn } from '../lib/cn';

export interface ChipProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'aria-pressed'> {
  /** Filter chip state. */
  pressed: boolean;
}

export function Chip({ pressed, className, type = 'button', ...rest }: ChipProps) {
  return (
    <button
      type={type}
      aria-pressed={pressed}
      className={cn(
        'inline-flex h-[30px] items-center rounded-pill px-3.5 text-secondary font-medium transition-colors',
        pressed
          ? 'bg-ink text-surface'
          : 'border border-control bg-surface text-text-2 hover:bg-bg',
        className,
      )}
      {...rest}
    />
  );
}
