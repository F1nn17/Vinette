import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { labelColor } from '@/shared/lib';
import { LabelDot } from '@/shared/ui';
import type { Label } from './api';
import { colorsOf, type LabelState } from './label-state';

function LabelRow({
  label,
  state,
  colors,
  onToggle,
}: {
  label: Label;
  state: LabelState;
  colors: Record<string, string>;
  onToggle: (on: boolean) => void;
}) {
  const box = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (box.current) box.current.indeterminate = state === 'some';
  }, [state]);
  return (
    <label className="flex min-h-11 cursor-pointer items-center gap-3 rounded-sm px-3 text-base text-ink hover:bg-bg md:min-h-10">
      <input
        ref={box}
        type="checkbox"
        checked={state === 'all'}
        onChange={() => onToggle(state !== 'all')}
        className="size-[18px] shrink-0 cursor-pointer accent-accent"
      />
      <LabelDot color={labelColor(label.name, colors)} />
      <span className="min-w-0 flex-1 truncate">{label.name}</span>
    </label>
  );
}

export interface LabelMenuProps {
  labels: readonly Label[];
  /** How a label stands among the messages the menu is for. */
  stateOf: (name: string) => LabelState;
  /** Puts the label on all the messages (`true`) or takes it off all of them (`false`). */
  onToggle: (name: string, on: boolean) => void;
  /** A new label the user typed: put on the messages. */
  onCreate: (name: string) => void;
  onClose: () => void;
  /** Where the panel sits; the default is below the right edge of the nearest positioned parent. */
  className?: string;
}

/**
 * The "Labels" panel (VIN-078, approved by the owner): the labels with a box each, a field for a new one at the bottom.
 * It closes on Escape and on a click outside it; the button that opens it carries `data-labels-trigger`, so that a click
 * on it closes the panel instead of closing and opening it again.
 */
export function LabelMenu({
  labels,
  stateOf: stateFor,
  onToggle,
  onCreate,
  onClose,
  className,
}: LabelMenuProps) {
  const { t } = useTranslation();
  const root = useRef<HTMLDivElement>(null);
  const field = useRef<HTMLInputElement>(null);
  const [name, setName] = useState('');
  const heading = useId();
  const colors = colorsOf(labels);

  useEffect(() => {
    const first = root.current?.querySelector<HTMLInputElement>('input[type="checkbox"]');
    (first ?? field.current)?.focus();
  }, []);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
      }
    };
    const onPointer = (event: PointerEvent) => {
      const target = event.target as Element | null;
      if (root.current?.contains(target as Node)) return;
      if (target?.closest?.('[data-labels-trigger]')) return;
      onClose();
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('pointerdown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('pointerdown', onPointer);
    };
  }, [onClose]);

  const submit = () => {
    const text = name.trim();
    if (text.length === 0) return;
    setName('');
    onCreate(text);
  };

  return (
    <div
      ref={root}
      role="group"
      aria-labelledby={heading}
      className={
        className ??
        'absolute top-full right-3 z-10 mt-1 flex w-72 max-w-[calc(100vw-1.5rem)] flex-col rounded-md border border-line bg-surface p-1 shadow-card'
      }
    >
      <p
        id={heading}
        className="px-3 pt-2 pb-1 text-caps font-semibold tracking-wider text-muted uppercase"
      >
        {t('labels.menu')}
      </p>
      {labels.length === 0 ? (
        <p className="px-3 py-2 text-secondary text-muted">{t('labels.none')}</p>
      ) : (
        <div className="flex max-h-64 flex-col overflow-y-auto">
          {labels.map((label) => (
            <LabelRow
              key={label.name}
              label={label}
              state={stateFor(label.name)}
              colors={colors}
              onToggle={(on) => onToggle(label.name, on)}
            />
          ))}
        </div>
      )}
      <form
        className="mt-1 border-t border-subtle p-2"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <input
          ref={field}
          value={name}
          onChange={(event) => setName(event.target.value)}
          aria-label={t('labels.newField')}
          placeholder={t('labels.newPlaceholder')}
          maxLength={50}
          className="h-10 w-full rounded-sm border border-field bg-surface px-3 text-base text-ink placeholder:text-muted focus:border-accent focus:outline-2 focus:outline-offset-0 focus:outline-accent max-md:h-11"
        />
      </form>
    </div>
  );
}
