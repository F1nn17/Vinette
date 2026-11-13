import { Ellipsis, Plus } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { describeApiError } from '@/shared/api/describe-error';
import { cn, labelColor } from '@/shared/lib';
import { useToast } from '@/shared/toast';
import { Button, Dialog, IconButton, LabelDot, type LabelColor } from '@/shared/ui';
import { useCreateLabel, useDeleteLabel, useLabels, useUpdateLabel, type Label } from './api';
import { colorsOf } from './label-state';
import { labelPath } from './label-path';

const PALETTE: LabelColor[] = ['work', 'bills', 'personal'];

/** A box to type a label's name in: Enter saves, Escape or leaving the box cancels. */
function NameField({
  label,
  initial = '',
  onSave,
  onCancel,
}: {
  label: string;
  initial?: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState(initial);
  const input = useRef<HTMLInputElement>(null);
  const done = useRef(false);
  useEffect(() => {
    input.current?.focus();
    input.current?.select();
  }, []);
  const finish = (save: boolean) => {
    if (done.current) return;
    done.current = true;
    const text = value.trim();
    if (save && text.length > 0 && text !== initial) onSave(text);
    else onCancel();
  };
  return (
    <input
      ref={input}
      value={value}
      aria-label={label}
      maxLength={50}
      onChange={(event) => setValue(event.target.value)}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          finish(true);
        } else if (event.key === 'Escape') {
          event.preventDefault();
          event.stopPropagation();
          finish(false);
        }
      }}
      onBlur={() => finish(false)}
      className="h-10 w-full rounded-sm border border-field bg-surface px-3 text-base text-ink focus:border-accent focus:outline-2 focus:outline-offset-0 focus:outline-accent max-md:h-11"
    />
  );
}

interface ItemMenuProps {
  label: Label;
  color: LabelColor;
  onColor: (color: LabelColor) => void;
  onRename: () => void;
  onDelete: () => void;
  onClose: () => void;
}

/** What can be done with a label: its colour (the three of the design), a new name, deleting it. */
function ItemMenu({ label, color, onColor, onRename, onDelete, onClose }: ItemMenuProps) {
  const { t } = useTranslation();
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    root.current?.querySelector<HTMLElement>('button')?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
      }
    };
    const onPointer = (event: PointerEvent) => {
      const target = event.target as Element | null;
      if (root.current?.contains(target as Node) || target?.closest?.('[data-label-menu-trigger]'))
        return;
      onClose();
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('pointerdown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('pointerdown', onPointer);
    };
  }, [onClose]);
  const item =
    'flex min-h-11 w-full items-center rounded-sm px-3 text-left text-base text-ink hover:bg-bg focus-visible:outline-2 focus-visible:outline-accent md:min-h-10';
  return (
    <div
      ref={root}
      role="group"
      aria-label={t('labels.actionsOf', { name: label.name })}
      className="absolute top-full right-0 z-10 mt-1 flex w-56 flex-col rounded-md border border-line bg-surface p-1 shadow-card"
    >
      <div
        role="group"
        aria-label={t('labels.color')}
        className="flex items-center gap-1 px-1 py-1"
      >
        {PALETTE.map((c) => (
          <button
            key={c}
            type="button"
            aria-pressed={c === color}
            aria-label={t(`labels.colors.${c}`)}
            title={t(`labels.colors.${c}`)}
            onClick={() => {
              onColor(c);
              onClose();
            }}
            className={cn(
              'inline-flex size-10 items-center justify-center rounded-sm hover:bg-bg focus-visible:outline-2 focus-visible:outline-accent max-md:size-11',
              c === color && 'bg-accent-soft',
            )}
          >
            <LabelDot color={c} />
          </button>
        ))}
      </div>
      <button type="button" className={item} onClick={onRename}>
        {t('labels.rename')}
      </button>
      <button type="button" className={item} onClick={onDelete}>
        {t('labels.delete')}
      </button>
    </div>
  );
}

interface ItemProps {
  label: Label;
  colors: Record<string, string>;
  active: boolean;
  onNavigate: () => void;
}

function LabelItem({ label, colors, active, onNavigate }: ItemProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const navigate = useNavigate();
  const update = useUpdateLabel();
  const remove = useDeleteLabel();
  const [mode, setMode] = useState<'idle' | 'menu' | 'rename' | 'confirm'>('idle');
  const color = labelColor(label.name, colors);
  const failed = (error: unknown) =>
    toast.show({ tone: 'error', message: describeApiError(error, t) });

  if (mode === 'rename') {
    return (
      <NameField
        label={t('labels.renameField', { name: label.name })}
        initial={label.name}
        onCancel={() => setMode('idle')}
        onSave={(newName) => {
          setMode('idle');
          // `mutateAsync`, not callbacks of `mutate`: the row goes away as the list is read again, and callbacks of a
          // component that is gone are not called.
          update
            .mutateAsync({ name: label.name, newName })
            .then((saved) => {
              if (active) void navigate(labelPath(saved.name), { replace: true });
            })
            .catch(failed);
        }}
      />
    );
  }

  return (
    <div className="group relative flex items-center">
      <Link
        to={labelPath(label.name)}
        onClick={onNavigate}
        aria-current={active ? 'page' : undefined}
        className={cn(
          'flex h-9 min-w-0 flex-1 items-center gap-3 rounded-sm px-3 text-base transition-colors max-md:h-11',
          active ? 'bg-accent-soft font-semibold text-accent' : 'text-text-2 hover:bg-surface',
        )}
      >
        <LabelDot color={color} />
        <span className="min-w-0 flex-1 truncate">{label.name}</span>
      </Link>
      <IconButton
        aria-label={t('labels.actionsOf', { name: label.name })}
        aria-expanded={mode === 'menu'}
        data-label-menu-trigger
        onClick={() => setMode(mode === 'menu' ? 'idle' : 'menu')}
        className={cn(
          'absolute right-0 size-9 max-md:size-11',
          mode !== 'menu' &&
            'md:opacity-0 md:group-focus-within:opacity-100 md:group-hover:opacity-100',
        )}
      >
        <Ellipsis size={16} strokeWidth={1.8} aria-hidden />
      </IconButton>
      {mode === 'menu' && (
        <ItemMenu
          label={label}
          color={color}
          onColor={(next) =>
            void update.mutateAsync({ name: label.name, color: next }).catch(failed)
          }
          onRename={() => setMode('rename')}
          onDelete={() => setMode('confirm')}
          onClose={() => setMode('idle')}
        />
      )}
      <Dialog
        open={mode === 'confirm'}
        onClose={() => setMode('idle')}
        title={t('labels.deleteTitle', { name: label.name })}
        closeLabel={t('common.close')}
      >
        <p className="text-base text-text-2">{t('labels.deleteText')}</p>
        <div className="mt-5 flex justify-end gap-2">
          <Button variant="secondary" onClick={() => setMode('idle')}>
            {t('common.cancel')}
          </Button>
          <Button
            onClick={() => {
              setMode('idle');
              remove
                .mutateAsync({ name: label.name })
                .then(() => {
                  if (active) void navigate('/mail/inbox', { replace: true });
                })
                .catch(failed);
            }}
          >
            {t('labels.deleteConfirm')}
          </Button>
        </div>
      </Dialog>
    </div>
  );
}

export interface SidebarLabelsProps {
  /** The label whose list is open, if any. */
  activeLabel?: string;
  /** Called when a label is chosen (the drawer of a phone closes). */
  onNavigate: () => void;
}

/**
 * The labels of the sidebar (DESIGN.md §5, approved by the owner in VIN-078): the heading with "+", which turns into a field
 * for the name of a new label, and a row per label that opens the list of its messages; the "..." of a row opens its colour,
 * renaming and deleting.
 */
export function SidebarLabels({ activeLabel, onNavigate }: SidebarLabelsProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const labels = useLabels();
  const create = useCreateLabel();
  const [adding, setAdding] = useState(false);
  const heading = useId();
  const colors = colorsOf(labels.data);
  const wanted = activeLabel?.toLowerCase();

  return (
    <section aria-labelledby={heading} className="flex flex-col gap-0.5 px-1.5">
      <div className="flex items-center justify-between">
        <h2
          id={heading}
          className="py-1.5 pl-1.5 text-caps font-semibold tracking-wider text-muted uppercase"
        >
          {t('sidebar.labels')}
        </h2>
        <IconButton
          aria-label={t('labels.add')}
          aria-expanded={adding}
          onClick={() => setAdding((value) => !value)}
          className="size-9 max-md:size-11"
        >
          <Plus size={16} strokeWidth={1.8} aria-hidden />
        </IconButton>
      </div>
      {adding && (
        <NameField
          label={t('labels.newField')}
          onCancel={() => setAdding(false)}
          onSave={(name) => {
            setAdding(false);
            create.mutate(
              { name },
              {
                onError: (error) =>
                  toast.show({ tone: 'error', message: describeApiError(error, t) }),
              },
            );
          }}
        />
      )}
      {labels.isError ? (
        <div role="alert" className="text-secondary text-text-2">
          <p>{describeApiError(labels.error, t)}</p>
          <Button variant="ghost" size="sm" onClick={() => void labels.refetch()}>
            {t('common.retry')}
          </Button>
        </div>
      ) : (
        labels.data?.length === 0 &&
        !adding && <p className="px-1.5 text-secondary text-muted">{t('sidebar.noLabels')}</p>
      )}
      {labels.data?.map((label) => (
        <LabelItem
          key={label.name}
          label={label}
          colors={colors}
          active={wanted === label.name.toLowerCase()}
          onNavigate={onNavigate}
        />
      ))}
    </section>
  );
}
