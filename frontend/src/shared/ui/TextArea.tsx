import { useId, type TextareaHTMLAttributes } from 'react';
import { cn } from '../lib/cn';
import { fieldClass } from './fields';

export interface TextAreaProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id'> {
  label: string;
  hint?: string;
  error?: string;
}

export function TextArea({ label, hint, error, className, rows = 4, ...rest }: TextAreaProps) {
  const id = useId();
  const noteId = `${id}-note`;
  const note = error ?? hint;
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={id} className="text-secondary font-medium text-text-2">
        {label}
      </label>
      <textarea
        id={id}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={note ? noteId : undefined}
        className={cn(fieldClass, 'resize-y py-2.5 leading-normal')}
        {...rest}
      />
      {note && (
        <p id={noteId} className={cn('text-meta', error ? 'text-warning' : 'text-muted')}>
          {note}
        </p>
      )}
    </div>
  );
}
