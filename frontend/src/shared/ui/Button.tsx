import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cn } from '../lib/cn';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost';
export type ButtonSize = 'sm' | 'md' | 'lg' | 'xl';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  /** sm 36px, md 40px, lg 44px, xl 48px */
  size?: ButtonSize;
  icon?: ReactNode;
  fullWidth?: boolean;
}

const variants: Record<ButtonVariant, string> = {
  primary: 'bg-accent text-on-accent hover:bg-accent-hover',
  secondary: 'border border-control bg-surface text-text-2 hover:bg-bg',
  ghost: 'bg-transparent text-text-2 hover:bg-bg',
};

const sizes: Record<ButtonSize, string> = {
  sm: 'h-9 rounded-xs px-3',
  md: 'h-10 rounded-md px-4',
  lg: 'h-11 rounded-md px-5',
  xl: 'h-12 rounded-md px-5',
};

export function Button({
  variant = 'primary',
  size = 'md',
  icon,
  fullWidth,
  className,
  children,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      className={cn(
        'inline-flex items-center justify-center gap-2 text-base font-semibold whitespace-nowrap transition-colors',
        'disabled:cursor-not-allowed disabled:opacity-50',
        variants[variant],
        sizes[size],
        fullWidth && 'w-full',
        className,
      )}
      {...rest}
    >
      {icon}
      {children}
    </button>
  );
}
