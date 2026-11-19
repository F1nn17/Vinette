import { Plus } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { describeApiError } from '@/shared/api/describe-error';
import { cn } from '@/shared/lib/cn';
import { Button, Checkbox, TextArea, TextField } from '@/shared/ui';
import { useSaveSignatures, useSignatures, type Signature } from './signatures';

/** The longest name and text the server accepts (UserSettings), so the fields stop where the server would refuse. */
const MAX_NAME = 60;
const MAX_TEXT = 2000;

/** A signature being edited: a saved one keeps its id, a new one has none yet. */
interface Draft {
  key: string;
  id: string | null;
  name: string;
  text: string;
  default: boolean;
}

const fromSaved = (s: Signature): Draft => ({
  key: s.id,
  id: s.id,
  name: s.name,
  text: s.text,
  default: s.default,
});

export interface SignaturesEditorProps {
  /** Called once the list is saved. */
  onSaved?: () => void;
  /** Called when the user gives up. */
  onCancel?: () => void;
}

/**
 * Making, changing and deleting signatures, choosing the one for new messages, with a preview of how the text will stand under a message.
 * The whole list is edited here and saved at once; nothing is kept until "Save". Used in the editor's window and, later, on the settings screen.
 */
export function SignaturesEditor(props: SignaturesEditorProps) {
  const { t } = useTranslation();
  const saved = useSignatures();
  // The list is copied once, when it is known: what is typed here must not be overwritten by a later reading of the settings.
  if (saved.loading) return <p className="text-muted">{t('common.loading')}</p>;
  return <Form initial={saved.list} {...props} />;
}

function Form({ initial, onSaved, onCancel }: SignaturesEditorProps & { initial: Signature[] }) {
  const { t } = useTranslation();
  const save = useSaveSignatures();
  const [drafts, setDrafts] = useState<Draft[]>(() => initial.map(fromSaved));
  const [current, setCurrent] = useState<string | null>(drafts[0]?.key ?? null);
  const [counter, setCounter] = useState(0);
  const active = drafts.find((d) => d.key === current) ?? null;

  const change = (key: string, patch: Partial<Draft>) =>
    setDrafts((all) =>
      all.map((d) => {
        if (d.key === key) return { ...d, ...patch };
        // only one is the default
        return patch.default === true ? { ...d, default: false } : d;
      }),
    );

  const add = () => {
    const key = `new-${counter + 1}`;
    setCounter(counter + 1);
    setDrafts((all) => [...all, { key, id: null, name: '', text: '', default: all.length === 0 }]);
    setCurrent(key);
  };

  const remove = (key: string) => {
    const rest = drafts.filter((d) => d.key !== key);
    setDrafts(rest);
    setCurrent(rest[0]?.key ?? null);
  };

  const nameless = drafts.some((d) => d.name.trim() === '');

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <ul aria-label={t('signatures.list')} className="contents">
          {drafts.map((d) => (
            <li key={d.key}>
              <button
                type="button"
                aria-pressed={d.key === current}
                onClick={() => setCurrent(d.key)}
                className={cn(
                  'inline-flex h-9 items-center rounded-pill border px-3.5 text-secondary font-medium max-md:h-11',
                  d.key === current
                    ? 'border-accent bg-accent-soft text-accent'
                    : 'border-control bg-surface text-text-2 hover:bg-bg',
                )}
              >
                {d.name.trim() === '' ? t('signatures.unnamed') : d.name}
                {d.default && <span className="sr-only"> ({t('signatures.defaultMark')})</span>}
              </button>
            </li>
          ))}
        </ul>
        <Button
          variant="secondary"
          size="sm"
          className="max-md:h-11"
          icon={<Plus size={16} strokeWidth={1.8} aria-hidden />}
          onClick={add}
        >
          {t('signatures.add')}
        </Button>
      </div>

      {drafts.length === 0 && <p className="text-secondary text-muted">{t('signatures.none')}</p>}

      {active && (
        <div className="flex flex-col gap-3">
          <TextField
            label={t('signatures.name')}
            value={active.name}
            maxLength={MAX_NAME}
            onChange={(e) => change(active.key, { name: e.target.value })}
          />
          <TextArea
            label={t('signatures.text')}
            rows={5}
            value={active.text}
            maxLength={MAX_TEXT}
            onChange={(e) => change(active.key, { text: e.target.value })}
          />
          <Checkbox
            label={t('signatures.makeDefault')}
            checked={active.default}
            onChange={(e) => change(active.key, { default: e.target.checked })}
          />
          <div>
            <p className="text-secondary font-medium text-text-2">{t('signatures.preview')}</p>
            <div
              aria-label={t('signatures.preview')}
              role="group"
              className="mt-1.5 min-h-12 rounded-sm bg-bg px-3 py-2 text-base whitespace-pre-wrap text-text-2"
            >
              {active.text}
            </div>
          </div>
          <div>
            <Button
              variant="ghost"
              size="sm"
              className="max-md:h-11"
              onClick={() => remove(active.key)}
            >
              {t('signatures.delete')}
            </Button>
          </div>
        </div>
      )}

      {save.isError && (
        <p role="alert" className="text-secondary text-warning">
          {describeApiError(save.error, t)}
        </p>
      )}
      {nameless && drafts.length > 0 && (
        <p className="text-meta text-muted">{t('signatures.nameNeeded')}</p>
      )}

      <div className="flex justify-end gap-2">
        {onCancel && (
          <Button variant="secondary" onClick={onCancel}>
            {t('common.cancel')}
          </Button>
        )}
        <Button
          disabled={save.isPending || nameless}
          onClick={() =>
            save.mutate(
              drafts.map((d) => ({ id: d.id, name: d.name, text: d.text, default: d.default })),
              { onSuccess: () => onSaved?.() },
            )
          }
        >
          {t('signatures.save')}
        </Button>
      </div>
    </div>
  );
}
