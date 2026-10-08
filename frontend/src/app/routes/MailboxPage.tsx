import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

export function MailboxPage() {
  const { t } = useTranslation();
  const { folder } = useParams();
  return (
    <main className="p-8">
      <h1 className="text-2xl font-semibold">{t('mailbox.title')}</h1>
      <p className="text-muted">{folder}</p>
    </main>
  );
}
