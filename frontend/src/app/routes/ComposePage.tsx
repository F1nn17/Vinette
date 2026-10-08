import { useTranslation } from 'react-i18next';

export function ComposePage() {
  const { t } = useTranslation();
  return (
    <main className="p-8">
      <h1 className="text-2xl font-semibold">{t('compose.title')}</h1>
    </main>
  );
}
