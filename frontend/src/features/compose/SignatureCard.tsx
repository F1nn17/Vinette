import { useTranslation } from 'react-i18next';
import { Button, Select } from '@/shared/ui';

export interface SignatureOption {
  id: string;
  name: string;
  text: string;
}

export interface SignatureCardProps {
  signatures: SignatureOption[];
  /** The id of the chosen signature; empty for none. */
  selectedId: string;
  onSelect: (id: string) => void;
  /** Opens the window where signatures are made and changed. */
  onManage: () => void;
}

/**
 * "Signature" of the editor's right column (DESIGN.md §6): the choice and a preview of the chosen one. The signature goes under the message
 * when it is sent; it is not in the text being written.
 */
export function SignatureCard({ signatures, selectedId, onSelect, onManage }: SignatureCardProps) {
  const { t } = useTranslation();
  const chosen = signatures.find((s) => s.id === selectedId) ?? null;
  return (
    <section
      aria-label={t('compose.signature.title')}
      className="flex flex-col gap-3 rounded-xl border border-line bg-surface p-5"
    >
      {signatures.length === 0 ? (
        <>
          <h2 className="text-secondary font-semibold text-ink">{t('compose.signature.title')}</h2>
          <p className="text-secondary text-muted">{t('compose.signature.none')}</p>
        </>
      ) : (
        <>
          <Select
            label={t('compose.signature.title')}
            value={selectedId}
            onChange={(e) => onSelect(e.target.value)}
          >
            <option value="">{t('compose.signature.without')}</option>
            {signatures.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </Select>
          {chosen && (
            <div
              role="group"
              aria-label={t('compose.signature.preview')}
              className="rounded-sm bg-bg px-3 py-2.5 text-secondary whitespace-pre-wrap text-text-2"
            >
              {chosen.text}
            </div>
          )}
        </>
      )}
      <div>
        <Button variant="secondary" size="sm" className="max-md:h-11" onClick={onManage}>
          {t(signatures.length === 0 ? 'compose.signature.create' : 'compose.signature.manage')}
        </Button>
      </div>
    </section>
  );
}
