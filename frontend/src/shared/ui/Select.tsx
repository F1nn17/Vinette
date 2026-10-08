import { ChevronDown } from 'lucide-react';
import { useId, type SelectHTMLAttributes } from 'react';
import { cn } from '../lib/cn';
import { fieldClass } from './fields';

export interface SelectProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id'> {
  label: string;
}

export function Select({ label, className, children, ...rest }: SelectProps) {
  const id = useId();
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={id} className="text-secondary font-medium text-text-2">
        {label}
      </label>
      <div className="relative">
        <select id={id} className={cn(fieldClass, 'h-11 appearance-none pr-10')} {...rest}>
          {children}
        </select>
        <ChevronDown
          aria-hidden="true"
          size={18}
          strokeWidth={1.8}
          className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-muted"
        />
      </div>
    </div>
  );
}
