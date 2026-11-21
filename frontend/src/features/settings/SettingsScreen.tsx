import { X } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { describeApiError } from '@/shared/api/describe-error';
import { Chip, IconButton, Select } from '@/shared/ui';
import { LANGUAGES } from '@/shared/i18n/language';
import { useSettings, useUntrustImageSender, useUpdateSettings, type ListDensity } from './api';
import { SignaturesEditor } from './SignaturesEditor';

const DENSITIES: ListDensity[] = ['comfortable', 'compact'];

function Section({
  id,
  title,
  hint,
  children,
}: {
  id: string;
  title: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <section
      aria-labelledby={id}
      className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-5 max-md:p-4"
    >
      <div className="flex flex-col gap-1">
        <h2 id={id} className="text-lead font-bold text-ink">
          {title}
        </h2>
        {hint && <p className="text-secondary text-muted">{hint}</p>}
      </div>
      {children}
    </section>
  );
}

/**
 * The settings (DESIGN.md §5, "Настройки", decided by the owner in VIN-112): the look of the list, the language, the signatures and the senders
 * whose pictures are shown without asking. Every change is saved on the server at once and shown at once, there is no "Save" button
 * (only the signatures are a form of their own, as a whole list is edited there).
 */
export function SettingsScreen() {
  const { t } = useTranslation();
  const settings = useSettings();
  const update = useUpdateSettings();
  const untrust = useUntrustImageSender();

  if (settings.isPending) {
    return (
      <p role="status" className="p-6 text-muted">
        {t('common.loading')}
      </p>
    );
  }
  if (settings.isError) {
    return (
      <p role="alert" className="p-6 text-text-2">
        {describeApiError(settings.error, t)}
      </p>
    );
  }

  const data = settings.data;
  const language = LANGUAGES.find((l) => l === data.language) ?? '';
  const senders = data.trustedImageSenders;

  return (
    <div className="flex flex-col gap-5">
      <Section id="settings-look" title={t('settings.look.title')}>
        <div role="group" aria-labelledby="settings-density" className="flex flex-col gap-2">
          <p id="settings-density" className="text-secondary font-medium text-text-2">
            {t('settings.density.title')}
          </p>
          <div className="flex flex-wrap gap-2">
            {DENSITIES.map((d) => (
              <Chip
                key={d}
                pressed={data.density === d}
                onClick={() => update.mutate({ density: d })}
              >
                {t(`settings.density.${d}`)}
              </Chip>
            ))}
          </div>
          <p className="text-secondary text-muted">{t('settings.density.hint')}</p>
        </div>
        <Select
          label={t('settings.language.title')}
          value={language}
          onChange={(event) => update.mutate({ language: event.target.value })}
          className="max-w-xs"
        >
          <option value="">{t('settings.language.browser')}</option>
          {LANGUAGES.map((l) => (
            <option key={l} value={l}>
              {t(`settings.language.${l}`)}
            </option>
          ))}
        </Select>
        {update.isError && (
          <p role="alert" className="text-secondary text-warning">
            {describeApiError(update.error, t)}
          </p>
        )}
      </Section>

      <Section
        id="settings-signatures"
        title={t('signatures.title')}
        hint={t('settings.signatures.hint')}
      >
        <SignaturesEditor />
      </Section>

      <Section
        id="settings-images"
        title={t('settings.images.title')}
        hint={t('settings.images.hint')}
      >
        {senders.length === 0 ? (
          <p className="text-muted">{t('settings.images.none')}</p>
        ) : (
          <ul
            aria-label={t('settings.images.title')}
            className="flex flex-col divide-y divide-subtle"
          >
            {senders.map((address) => (
              <li key={address} className="flex items-center justify-between gap-3 py-2">
                <span className="min-w-0 truncate text-base text-ink">{address}</span>
                <IconButton
                  aria-label={t('settings.images.remove', { address })}
                  disabled={untrust.isPending}
                  onClick={() => untrust.mutate(address)}
                >
                  <X size={18} strokeWidth={1.8} aria-hidden />
                </IconButton>
              </li>
            ))}
          </ul>
        )}
        {untrust.isError && (
          <p role="alert" className="text-secondary text-warning">
            {describeApiError(untrust.error, t)}
          </p>
        )}
      </Section>
    </div>
  );
}
