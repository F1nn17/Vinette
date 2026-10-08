import type { ButtonHTMLAttributes } from 'react';

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'ghost' };

export function Button({ variant = 'primary', className = '', ...rest }: Props) {
  const styles =
    variant === 'primary'
      ? 'bg-accent text-white hover:opacity-90'
      : 'bg-transparent text-fg hover:bg-surface-muted';
  return (
    <button
      type="button"
      className={`rounded-md px-4 py-2 text-sm font-medium transition ${styles} ${className}`}
      {...rest}
    />
  );
}
