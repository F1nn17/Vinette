import { useTranslation } from 'react-i18next';
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
import { describeApiError } from '@/shared/api/describe-error';

/** The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`. Sidebar, list and reading area. */
export function MailboxPage() {
  const { folder = 'inbox', messageId } = useParams();
  const navigate = useNavigate();
  const { search } = useLocation();
  const { address } = useSession();
  const layout = useLayout();
  const { t } = useTranslation();
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
          actionBusy={batch.isPending}
          actionError={batch.isError ? describeApiError(batch.error, t) : null}
          onAction={(action) => {
            if (messageId === undefined) return;
            // The message is no longer in this list afterwards (or is unread again): back to the list.
            batch.mutate({ ids: [messageId], action }, { onSuccess: () => void navigate(toList) });
          }}
        />
      }
    />
  );
}
