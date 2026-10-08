import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cn } from '../lib/cn';

export interface IconButtonProps extends Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  'aria-label'
> {
  /** Required: an icon-only button has no other accessible name. */
  'aria-label': string;
  children: ReactNode;
  variant?: 'ghost' | 'secondary';
  /** Toggle state (e.g. flag). Renders aria-pressed. */
  pressed?: boolean;
}

export function IconButton({
  variant = 'ghost',
  pressed,
  className,
  type = 'button',
  ...rest
}: IconButtonProps) {
  return (
    <button
      type={type}
      aria-pressed={pressed}
      className={cn(
        'inline-flex size-10 items-center justify-center rounded-sm transition-colors max-md:size-11',
        'disabled:cursor-not-allowed disabled:opacity-50',
        variant === 'secondary' ? 'border border-control bg-surface hover:bg-bg' : 'hover:bg-bg',
        pressed ? 'text-flag' : 'text-text-2',
        className,
      )}
      {...rest}
    />
  );
}
