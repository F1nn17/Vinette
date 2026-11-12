import { useLocation, useNavigate, useParams } from 'react-router';
import { useSession } from '@/features/auth';
import { AppShell, MessageListPane, Sidebar, useLayout } from '@/features/mailbox';
import { ReadingPane } from '@/features/message';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. Sidebar, list and reading area. */
export function MailboxPage() {
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();
  const { search } = useLocation();
  const { address } = useSession();
  const layout = useLayout();

  return (
    <AppShell
      messageOpen={messageId !== undefined}
      nav={<Sidebar address={address ?? ''} folderSlug={folder} />}
      list={
        <MessageListPane folderSlug={folder} messageId={messageId} address={address ?? undefined} />
      }
      reader={
        <ReadingPane
          messageId={messageId}
          address={address ?? undefined}
          onBack={
            layout === 'mobile'
              ? () => void navigate(`/mail/${encodeURIComponent(folder)}${search}`)
              : undefined
          }
        />
      }
    />
  );
}
