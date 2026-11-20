import { Link, useLocation, useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/features/auth';
import {
  ComposeScreen,
  REPLY_MODES,
  startOf,
  startOfDraft,
  useDraft,
  useQuote,
  withTypedText,
  type ReplyMode,
} from '@/features/compose';
import { describeApiError } from '@/shared/api/describe-error';

/**
 * `/compose`: a new message; `/compose?reply=<id>`, `?replyAll=<id>` and `?forward=<id>` start from the message with that id;
 * `/compose?draft=<id>` goes on with a draft.
 */
export function ComposePage() {
  const [params] = useSearchParams();
  const { address } = useSession();
  const draft = params.get('draft');
  if (draft) return <Resumed id={draft} address={address ?? ''} />;
  const mode = REPLY_MODES.find((m) => params.get(m));
  const id = mode ? params.get(mode) : null;
  if (mode && id) return <Answer mode={mode} id={id} address={address ?? ''} />;
  return <ComposeScreen address={address ?? ''} />;
}

/** The screen while the message to open is being read: the same wait and the same way back when it cannot be opened. */
function Waiting({ pending, error }: { pending: boolean; error: unknown }) {
  const { t } = useTranslation();
  if (pending) {
    return (
      <main role="status" className="p-8 text-muted">
        {t('compose.loadingMessage')}
      </main>
    );
  }
  return (
    <main className="flex flex-col items-start gap-3 p-8 text-text-2">
      <p role="alert">{describeApiError(error, t)}</p>
      <Link to="/mail/inbox" className="text-accent underline">
        {t('compose.back')}
      </Link>
    </main>
  );
}

/** Waits for the draft, then opens the editor on it. */
function Resumed({ id, address }: { id: string; address: string }) {
  const draft = useDraft(id);
  if (!draft.data) return <Waiting pending={draft.isPending} error={draft.error} />;
  return <ComposeScreen key={`draft-${id}`} address={address} start={startOfDraft(draft.data)} />;
}

/** Waits for the message to answer, then opens the editor on it. */
function Answer({ mode, id, address }: { mode: ReplyMode; id: string; address: string }) {
  const { t, i18n } = useTranslation();
  const quote = useQuote(mode, id);
  // What was typed in the quick reply before it was opened here: it goes above the quote, so that nothing is lost.
  const typed = (useLocation().state as { typed?: unknown } | null)?.typed;
  if (!quote.data) return <Waiting pending={quote.isPending} error={quote.error} />;
  return (
    <ComposeScreen
      key={`${mode}-${id}`}
      address={address}
      start={withTypedText(
        startOf(quote.data, mode, address, t, i18n.language),
        typeof typed === 'string' ? typed : '',
      )}
    />
  );
}
