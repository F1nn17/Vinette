import { useTranslation } from 'react-i18next';
import { Dialog } from '@/shared/ui';
import type { ShortcutName } from './shortcuts';

/** The keys as they are printed in the window, in the order of the list in DESIGN.md section 5. */
const ROWS: { name: ShortcutName; keys: string[] }[] = [
  { name: 'next', keys: ['j'] },
  { name: 'previous', keys: ['k'] },
  { name: 'open', keys: ['Enter'] },
  { name: 'archive', keys: ['e'] },
  { name: 'delete', keys: ['#'] },
  { name: 'labels', keys: ['l'] },
  { name: 'search', keys: ['/'] },
  { name: 'compose', keys: ['c'] },
  { name: 'help', keys: ['?'] },
];

/**
 * The "Keyboard shortcuts" window (DESIGN.md section 5): the only place the keys are explained, the screen itself has no
 * hints. Opened with `?`.
 */
export function ShortcutsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t('shortcuts.title')}
      closeLabel={t('common.close')}
    >
      <p className="mb-3 text-secondary text-muted">{t('shortcuts.note')}</p>
      <dl className="flex flex-col">
        {ROWS.map(({ name, keys }) => (
          <div
            key={name}
            className="flex items-center gap-3 border-b border-subtle py-2.5 last:border-b-0"
          >
            <dt className="min-w-0 flex-1 text-base text-ink">{t(`shortcuts.${name}`)}</dt>
            <dd className="flex shrink-0 gap-1">
              {keys.map((key) => (
                <kbd
                  key={key}
                  className="min-w-7 rounded-xs border border-line bg-bg px-2 py-0.5 text-center text-secondary font-semibold text-ink"
                >
                  {key}
                </kbd>
              ))}
            </dd>
          </div>
        ))}
      </dl>
    </Dialog>
  );
}
