import { useLocation, useNavigate, useParams } from 'react-router';
import { useSession } from '@/features/auth';
import { AppShell, MessageListPane, Sidebar } from '@/features/mailbox';
import { ReadingPane } from '@/features/message';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. Sidebar, list and reading area. */
export function MailboxPage() {
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();
  const { search } = useLocation();
  const { address } = useSession();

  return (
    <AppShell
      messageOpen={messageId !== undefined}
      onBack={() => void navigate(`/mail/${encodeURIComponent(folder)}${search}`)}
      nav={<Sidebar address={address ?? ''} folderSlug={folder} />}
      list={
        <MessageListPane folderSlug={folder} messageId={messageId} address={address ?? undefined} />
      }
      reader={<ReadingPane messageId={messageId} address={address ?? undefined} />}
    />
  );
}
