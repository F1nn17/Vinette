import { useParams } from 'react-router';
import { useSession } from '@/features/auth';
import { AppShell, Sidebar } from '@/features/mailbox';
import { ScheduledDetail, ScheduledList } from '@/features/scheduled';

/** `/mail/@scheduled` and `/mail/@scheduled/:id`: the messages put off with "Send later" (DESIGN.md §5, "Отложенные"). */
export function ScheduledPage() {
  const { id } = useParams();
  const { address, signOut } = useSession();
  return (
    <AppShell
      messageOpen={id !== undefined}
      nav={
        <Sidebar
          address={address ?? ''}
          folderSlug=""
          scheduledActive
          onSignOut={() => void signOut()}
        />
      }
      list={<ScheduledList openId={id} />}
      reader={<ScheduledDetail id={id} />}
    />
  );
}
