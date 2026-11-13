import { useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router';
import { useSession } from '@/features/auth';
import {
  AppShell,
  folderOfId,
  folderSlug,
  MessageListPane,
  Sidebar,
  useFolders,
  useLayout,
  useMessageBatch,
  usePurge,
} from '@/features/mailbox';
import { labelPath } from '@/features/labels';
import { ReadingPane } from '@/features/message';

/**
 * The mailbox screen: `/mail/:folder` and `/mail/:folder/:messageId`, or, for the messages of a label from all folders,
 * `/mail/@label/:name` and `/mail/@label/:name/:messageId`. Sidebar, list and reading area.
 */
export function MailboxPage() {
  const params = useParams();
  const labelName = params.name;
  const folder = labelName === undefined ? (params.folder ?? 'inbox') : '';
  const { messageId } = params;
  const navigate = useNavigate();
  const { search } = useLocation();
  const { address } = useSession();
  const layout = useLayout();
  const folders = useFolders();
  // A label's list holds messages of several folders: the open message says which folder it is in.
  const openFolder = messageId === undefined ? null : folderOfId(messageId);
  const role = folders.data?.find((f) =>
    labelName === undefined ? folderSlug(f) === folder : f.path === openFolder,
  )?.role;
  const batch = useMessageBatch();
  const purge = usePurge();
  const [labelsFor, setLabelsFor] = useState<string | null>(null); // the message whose "Labels" panel is open
  const toList = `${labelName === undefined ? `/mail/${encodeURIComponent(folder)}` : labelPath(labelName)}${search}`;

  return (
    <>
      {purge.dialog}
      <AppShell
        messageOpen={messageId !== undefined}
        nav={<Sidebar address={address ?? ''} folderSlug={folder} labelName={labelName} />}
        list={
          <MessageListPane
            folderSlug={folder}
            labelName={labelName}
            messageId={messageId}
            address={address ?? undefined}
            onOpenLabels={setLabelsFor}
          />
        }
        reader={
          <ReadingPane
            messageId={messageId}
            address={address ?? undefined}
            onBack={layout === 'mobile' ? () => void navigate(toList) : undefined}
            folderRole={role}
            labelsFor={labelsFor}
            onLabelsForChange={setLabelsFor}
            onAction={(action) => {
              if (messageId === undefined) return;
              if (action === 'delete') {
                // for good: asked first; the screen goes back to the list when it is confirmed
                purge.ask([messageId], () => void navigate(toList));
                return;
              }
              // The screen follows at once (see `useMessageBatch`): the message is no longer in this list afterwards (or is
              // unread again), so back to the list; if the server refuses, the list gets it back and a toast says so.
              batch.mutate({ ids: [messageId], action });
              void navigate(toList);
            }}
          />
        }
      />
    </>
  );
}
