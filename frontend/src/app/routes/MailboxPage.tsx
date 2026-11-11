import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';
import { AppShell } from '@/features/mailbox';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. The panes are placeholders until VIN-047…049. */
export function MailboxPage() {
  const { t } = useTranslation();
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();

  return (
    <AppShell
      messageOpen={messageId !== undefined}
      onBack={() => void navigate(`/mail/${encodeURIComponent(folder)}`)}
      nav={<h2 className="p-4 text-base font-semibold text-ink">{t('mailbox.title')}</h2>}
      list={<p className="p-5 text-muted">{folder}</p>}
      reader={
        <div className="p-8">
          <h1 className="text-subject font-bold text-ink">{t('mailbox.title')}</h1>
          <p className="text-muted">{messageId ?? t('mailbox.noMessage')}</p>
        </div>
      }
    />
  );
}
