import { useId, type InputHTMLAttributes } from 'react';
import { cn } from '../lib/cn';

export interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id' | 'type'> {
  label: string;
}

export function Checkbox({ label, className, ...rest }: CheckboxProps) {
  const id = useId();
  return (
    <div className={cn('flex min-h-10 items-center gap-2.5', className)}>
      <input
        id={id}
        type="checkbox"
        className="size-[18px] shrink-0 cursor-pointer accent-accent"
        {...rest}
      />
      <label htmlFor={id} className="cursor-pointer text-base text-text-2">
        {label}
      </label>
    </div>
  );
}
