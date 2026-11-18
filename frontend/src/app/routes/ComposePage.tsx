import { Link, useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/features/auth';
import { ComposeScreen, REPLY_MODES, startOf, useQuote, type ReplyMode } from '@/features/compose';
import { describeApiError } from '@/shared/api/describe-error';

/** `/compose`: a new message; `/compose?reply=<id>`, `?replyAll=<id>` and `?forward=<id>` start from the message with that id. */
export function ComposePage() {
  const [params] = useSearchParams();
  const { address } = useSession();
  const mode = REPLY_MODES.find((m) => params.get(m));
  const id = mode ? params.get(mode) : null;
  if (mode && id) return <Answer mode={mode} id={id} address={address ?? ''} />;
  return <ComposeScreen address={address ?? ''} />;
}

/** Waits for the message to answer, then opens the editor on it. */
function Answer({ mode, id, address }: { mode: ReplyMode; id: string; address: string }) {
  const { t, i18n } = useTranslation();
  const quote = useQuote(mode, id);
  if (quote.isPending) {
    return (
      <main role="status" className="p-8 text-muted">
        {t('compose.loadingMessage')}
      </main>
    );
  }
  if (quote.isError) {
    return (
      <main className="flex flex-col items-start gap-3 p-8 text-text-2">
        <p role="alert">{describeApiError(quote.error, t)}</p>
        <Link to="/mail/inbox" className="text-accent underline">
          {t('compose.back')}
        </Link>
      </main>
    );
  }
  return (
    <ComposeScreen
      key={`${mode}-${id}`}
      address={address}
      start={startOf(quote.data, mode, address, t, i18n.language)}
    />
  );
}
