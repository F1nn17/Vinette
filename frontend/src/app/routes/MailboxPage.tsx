import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate, useParams } from 'react-router';
import { useSession } from '@/features/auth';
import { AppShell, MessageListPane, Sidebar } from '@/features/mailbox';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. The panes are placeholders until VIN-047…049. */
export function MailboxPage() {
  const { t } = useTranslation();
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();
  const { search } = useLocation();
  const { address } = useSession();

  return (
    <AppShell
      messageOpen={messageId !== undefined}
      onBack={() => void navigate(`/mail/${encodeURIComponent(folder)}${search}`)}
      nav={<Sidebar address={address ?? ''} folderSlug={folder} />}
      list={<MessageListPane folderSlug={folder} messageId={messageId} />}
      reader={
        <div className="p-8">
          <h1 className="text-subject font-bold text-ink">{t('mailbox.title')}</h1>
          <p className="text-muted">{messageId ?? t('mailbox.noMessage')}</p>
        </div>
      }
    />
  );
}
