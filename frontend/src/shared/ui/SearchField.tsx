import { Search } from 'lucide-react';
import type { InputHTMLAttributes } from 'react';
import { cn } from '../lib/cn';

export interface SearchFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  /** Required: the field has no visible label, the placeholder is not a name. */
  'aria-label': string;
}

/** A search box: tinted field with a magnifier (DESIGN.md §5), 40 px high. */
export function SearchField({ className, ...rest }: SearchFieldProps) {
  return (
    <div className={cn('relative', className)}>
      <Search
        size={18}
        strokeWidth={1.8}
        aria-hidden
        className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-muted"
      />
      <input
        type="search"
        className={cn(
          'h-10 w-full rounded-sm border border-transparent bg-bg pr-3 pl-10 text-base text-ink placeholder:text-muted',
          'focus:border-accent focus:outline-2 focus:outline-offset-0 focus:outline-accent',
          'disabled:cursor-not-allowed disabled:opacity-50 max-md:h-11',
        )}
        {...rest}
      />
    </div>
  );
}
