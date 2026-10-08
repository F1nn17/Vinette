import { useId, type InputHTMLAttributes, type ReactNode } from 'react';
import { cn } from '../lib/cn';
import { fieldClass } from './fields';

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  /** Required: every field has a visible label. */
  label: string;
  hint?: string;
  error?: string;
  /** Trailing control inside the field (e.g. show/hide password). */
  endSlot?: ReactNode;
}

export function TextField({ label, hint, error, endSlot, className, ...rest }: TextFieldProps) {
  const id = useId();
  const noteId = `${id}-note`;
  const note = error ?? hint;
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={id} className="text-secondary font-medium text-text-2">
        {label}
      </label>
      <div className="relative">
        <input
          id={id}
          aria-invalid={error ? true : undefined}
          aria-describedby={note ? noteId : undefined}
          className={cn(fieldClass, 'h-11', endSlot ? 'pr-12' : undefined)}
          {...rest}
        />
        {endSlot && <div className="absolute inset-y-0 right-1 flex items-center">{endSlot}</div>}
      </div>
      {note && (
        <p id={noteId} className={cn('text-meta', error ? 'text-warning' : 'text-muted')}>
          {note}
        </p>
      )}
    </div>
  );
}
