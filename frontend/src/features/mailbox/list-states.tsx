import { WifiOff } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { describeApiError } from '@/shared/api/describe-error';
import { Button } from '@/shared/ui';

/** Placeholder rows while the list loads (DESIGN.md §9). */
export function ListSkeleton({ rows = 8 }: { rows?: number }) {
  const { t } = useTranslation();
  return (
    <div role="status" aria-label={t('common.loading')}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} data-testid="skeleton-row" className="border-b border-subtle px-5 py-3.5">
          <div className="h-3.5 w-1/3 animate-pulse rounded-xs bg-line" />
          <div className="mt-2 h-3.5 w-3/4 animate-pulse rounded-xs bg-line/70" />
          <div className="mt-2 h-3 w-full animate-pulse rounded-xs bg-line/50" />
        </div>
      ))}
    </div>
  );
}

/** Nothing to show (an empty folder or a filter that matches nothing, or a label no message has). */
export function ListEmpty({ label = false }: { label?: boolean }) {
  const { t } = useTranslation();
  return <p className="p-5 text-muted">{t(label ? 'labels.empty' : 'list.empty')}</p>;
}

/** The folder in the address does not exist. */
export function ListNoFolder() {
  const { t } = useTranslation();
  return <p className="p-5 text-muted">{t('list.noFolder')}</p>;
}

/** The list could not be loaded: what happened in plain words, and a way to try again. */
export function ListError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <div role="alert" className="flex flex-col items-start gap-2 p-5 text-text-2">
      <p>{describeApiError(error, t)}</p>
      <Button variant="secondary" size="sm" onClick={onRetry}>
        {t('common.retry')}
      </Button>
    </div>
  );
}

/** Shown while the browser is offline; what is on screen stays usable. */
export function OfflineBanner() {
  const { t } = useTranslation();
  return (
    <div
      role="status"
      className="flex items-center gap-2.5 border-t border-subtle bg-bg px-5 py-2.5 text-secondary text-text-2"
    >
      <WifiOff size={16} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
      <span>{t('list.offline')}</span>
    </div>
  );
}
