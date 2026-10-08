import { cn } from '../lib/cn';

export interface ProgressBarProps {
  /** Required accessible name, e.g. "Storage used". */
  'aria-label': string;
  value: number;
  max?: number;
  tone?: 'accent' | 'ink';
  className?: string;
}

export function ProgressBar({
  value,
  max = 100,
  tone = 'accent',
  className,
  'aria-label': ariaLabel,
}: ProgressBarProps) {
  const percent = Math.min(100, Math.max(0, (value / max) * 100));
  return (
    <div
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={max}
      aria-valuenow={value}
      aria-label={ariaLabel}
      className={cn('h-1.5 w-full overflow-hidden rounded-pill bg-line', className)}
    >
      <div
        className={cn(
          'h-full rounded-pill transition-[width]',
          tone === 'accent' ? 'bg-accent' : 'bg-ink',
        )}
        style={{ width: `${percent}%` }}
      />
    </div>
  );
}
