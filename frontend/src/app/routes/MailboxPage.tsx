import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';
import { useSession } from '@/features/auth';
import { AppShell, Sidebar } from '@/features/mailbox';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. The panes are placeholders until VIN-047…049. */
export function MailboxPage() {
  const { t } = useTranslation();
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();
  const { address } = useSession();

  return (
    <AppShell
      messageOpen={messageId !== undefined}
      onBack={() => void navigate(`/mail/${encodeURIComponent(folder)}`)}
      nav={<Sidebar address={address ?? ''} folderSlug={folder} />}
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
