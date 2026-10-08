import { useTranslation } from 'react-i18next';
import { getBranding } from '@/shared/lib/branding';

export function LoginPage() {
  const { t } = useTranslation();
  const { name } = getBranding();
  return (
    <main className="mx-auto max-w-sm p-8">
      <h1 className="text-2xl font-semibold">{t('login.title', { name })}</h1>
    </main>
  );
}
