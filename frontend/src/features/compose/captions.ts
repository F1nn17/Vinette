import type { TFunction } from 'i18next';

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

/**
 * "Last mail today", "yesterday" or on a date, for a person the mailbox has mail with; "Not in the address book" for anybody else
 * (DESIGN.md §6: the address book is the people the mailbox has mail with). Nothing until the answer is in.
 */
export function lastMailCaption(
  lastMail: string | null | undefined,
  now: Date,
  language: string,
  t: TFunction,
): string {
  if (lastMail === undefined) return '';
  if (lastMail === null) return t('compose.people.unknown');
  const date = new Date(lastMail);
  const days = Math.round((startOfDay(now) - startOfDay(date)) / 86_400_000);
  if (days <= 0) return t('compose.people.lastToday');
  if (days === 1) return t('compose.people.lastYesterday');
  const text = new Intl.DateTimeFormat(language, {
    day: 'numeric',
    month: 'short',
    ...(date.getFullYear() === now.getFullYear() ? {} : { year: 'numeric' }),
  }).format(date);
  return t('compose.people.lastOn', { date: text });
}
