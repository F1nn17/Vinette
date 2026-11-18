import { ChevronLeft } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { EMPTY_COMPOSE, type ComposeValues } from './compose-values';
import { ComposeForm } from './ComposeForm';
import { EMPTY_BODY, type ComposeBody } from './editor-text';
import { MessageEditor } from './MessageEditor';

export interface ComposeScreenProps {
  /** The mailbox the message goes from. */
  address: string;
}

/** The "New message" screen (DESIGN.md §6): a 64 px header and the form. */
export function ComposeScreen({ address }: ComposeScreenProps) {
  const { t } = useTranslation();
  const [values, setValues] = useState<ComposeValues>(EMPTY_COMPOSE);
  const [body, setBody] = useState<ComposeBody>(EMPTY_BODY);

  return (
    <div className="flex min-h-dvh flex-col bg-bg">
      <header className="flex h-16 shrink-0 items-center gap-4 border-b border-line bg-surface px-7 max-md:px-3">
        <Link
          to="/mail/inbox"
          aria-label={t('compose.backLabel')}
          className="inline-flex h-10 items-center gap-2 rounded-sm pr-3 pl-1 text-base font-medium text-text-2 hover:bg-bg max-md:h-11"
        >
          <ChevronLeft size={18} strokeWidth={1.8} aria-hidden />
          <span className="max-sm:sr-only">{t('compose.back')}</span>
        </Link>
        <span aria-hidden className="h-6 w-px bg-line" />
        <h1 className="text-logo font-semibold text-ink">{t('compose.title')}</h1>
      </header>
      <main className="min-w-0 flex-1 p-7 max-md:p-3">
        <ComposeForm address={address} values={values} onChange={setValues}>
          <MessageEditor value={body} onChange={setBody} />
        </ComposeForm>
      </main>
    </div>
  );
}
