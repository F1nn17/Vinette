import { cn } from '../lib/cn';
import { initials } from '../lib/initials';

export interface AvatarProps {
  name: string;
  /** Pixel size; default 42 (reader), 40 on the mobile list. */
  size?: number;
  /** Known contacts use the accent tint, others are neutral. */
  tone?: 'accent' | 'neutral';
  className?: string;
}

export function Avatar({ name, size = 42, tone = 'accent', className }: AvatarProps) {
  return (
    <span
      role="img"
      aria-label={name}
      style={{ width: size, height: size, fontSize: Math.round(size * 0.34) }}
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-full font-semibold select-none',
        tone === 'accent' ? 'bg-accent-soft text-accent' : 'bg-bg text-text-3',
        className,
      )}
    >
      {initials(name)}
    </span>
  );
}
