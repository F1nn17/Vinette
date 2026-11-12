import { useLocation, useNavigate, useParams } from 'react-router';
import { useSession } from '@/features/auth';
import {
  AppShell,
  folderSlug,
  MessageListPane,
  Sidebar,
  useFolders,
  useLayout,
  useMessageBatch,
} from '@/features/mailbox';
import { ReadingPane } from '@/features/message';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. Sidebar, list and reading area. */
export function MailboxPage() {
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();
  const { search } = useLocation();
  const { address } = useSession();
  const layout = useLayout();
  const folders = useFolders();
  const role = folders.data?.find((f) => folderSlug(f) === folder)?.role;
  const batch = useMessageBatch();
  const toList = `/mail/${encodeURIComponent(folder)}${search}`;

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
          onBack={layout === 'mobile' ? () => void navigate(toList) : undefined}
          folderRole={role}
          onAction={(action) => {
            if (messageId === undefined) return;
            // The screen follows at once (see `useMessageBatch`): the message is no longer in this list afterwards (or is
            // unread again), so back to the list; if the server refuses, the list gets it back and a toast says so.
            batch.mutate({ ids: [messageId], action });
            void navigate(toList);
          }}
        />
      }
    />
  );
}
