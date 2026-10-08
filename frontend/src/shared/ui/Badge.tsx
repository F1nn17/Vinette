import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

export function Badge({
  tone,
  icon,
  children,
  className,
}: {
  tone: 'success' | 'warning';
  icon?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      data-tone={tone}
      className={cn(
        'inline-flex items-center gap-1.5 rounded-sm px-2.5 py-1 text-meta font-medium',
        tone === 'success' ? 'bg-success-bg text-success' : 'bg-warning-bg text-warning',
        className,
      )}
    >
      {icon}
      {children}
    </span>
  );
}
