import { useSession } from '@/features/auth';
import { ComposeScreen } from '@/features/compose';

/** `/compose`: a new message. */
export function ComposePage() {
  const { address } = useSession();
  return <ComposeScreen address={address ?? ''} />;
}
