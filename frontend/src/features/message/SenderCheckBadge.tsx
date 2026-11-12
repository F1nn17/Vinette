import { ShieldAlert, ShieldCheck } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { SenderVerdict } from './api';

/**
 * What our mail server's checks say about the sender (DESIGN.md §5): a small confirmation for a verified one, a warning
 * block for a suspicious one, nothing when there is nothing reliable to say.
 */
export function SenderCheckBadge({ verdict }: { verdict: SenderVerdict }) {
  const { t } = useTranslation();
  if (verdict === 'verified') {
    return (
      <p className="inline-flex items-center gap-1.5 rounded-sm bg-success-bg px-2.5 py-1 text-meta font-medium text-success">
        <ShieldCheck size={14} strokeWidth={1.8} aria-hidden />
        {t('reader.verified')}
      </p>
    );
  }

  if (verdict === 'suspicious') {
    return (
      <div
        role="alert"
        className="flex items-start gap-2.5 rounded-md bg-warning-bg px-4 py-3 text-secondary text-warning"
      >
        <ShieldAlert size={18} strokeWidth={1.8} aria-hidden className="mt-0.5 shrink-0" />
        <p>{t('reader.suspicious')}</p>
      </div>
    );
  }

  return null;
}
