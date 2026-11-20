import { useTranslation } from 'react-i18next';
import { Avatar } from '@/shared/ui';
import { lastMailCaption } from './captions';
import { useRecipientLookup } from './lookup';
import { isValidAddress, recipientLabel, withoutDuplicates, type Recipient } from './recipients';

export interface RecipientsCardProps {
  /** Everybody the message goes to (To, Cc and Bcc), in the order they were added. */
  recipients: Recipient[];
}

/**
 * The "Recipients" card of the editor's right column (DESIGN.md §6): an avatar, the name and what the mailbox knows of each person. A person
 * the mailbox has mail with has the avatar in the accent tint, a stranger a neutral one. A recipient that is not an address is left out: the
 * field already shows the mistake.
 */
export function RecipientsCard({ recipients }: RecipientsCardProps) {
  const { t, i18n } = useTranslation();
  const people = withoutDuplicates(recipients).filter((r) => isValidAddress(r.address));
  const known = useRecipientLookup(people.map((r) => r.address));
  const now = new Date();
  return (
    <section
      aria-label={t('compose.people.title')}
      className="flex flex-col gap-3 rounded-xl border border-line bg-surface p-5"
    >
      <h2 className="text-secondary font-semibold text-ink">{t('compose.people.title')}</h2>
      {people.length === 0 ? (
        <p className="text-secondary text-muted">{t('compose.people.none')}</p>
      ) : (
        <ul className="flex flex-col gap-3">
          {people.map((r) => {
            const last = known.get(r.address.toLowerCase());
            return (
              <li key={r.address.toLowerCase()} className="flex items-center gap-3">
                <span aria-hidden>
                  <Avatar
                    name={recipientLabel(r)}
                    size={34}
                    tone={typeof last === 'string' ? 'accent' : 'neutral'}
                  />
                </span>
                <span className="min-w-0">
                  <span className="block truncate text-base font-medium text-ink" title={r.address}>
                    {recipientLabel(r)}
                  </span>
                  <span className="block truncate text-meta text-muted">
                    {lastMailCaption(last, now, i18n.language, t)}
                  </span>
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
