const DAY = 24 * 60 * 60 * 1000;

export interface ListDateWords {
  /** The word for yesterday ("Вчера"). */
  yesterday: string;
}

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

/** Whole calendar days from `date` to `now` by the user's local midnights; 0 for today, negative for the future. */
export const calendarDaysAgo = (date: Date, now: Date): number =>
  Math.round((startOfDay(now) - startOfDay(date)) / DAY);

const capitalize = (text: string) => text.charAt(0).toLocaleUpperCase() + text.slice(1);

/**
 * A date for a line of the message list (DESIGN.md §10): today "14:32", yesterday "Yesterday", within the last week the
 * weekday "Tue", earlier "2 Oct", in earlier years "2 Oct 2025". Day borders are the user's local midnights.
 */
export function formatListDate(
  date: Date,
  now: Date,
  language: string,
  words: ListDateWords,
): string {
  const days = calendarDaysAgo(date, now);
  if (days <= 0) {
    // Also a message "from the future" (a clock that is off) shows as a time, not as a date.
    return new Intl.DateTimeFormat(language, { hour: '2-digit', minute: '2-digit' }).format(date);
  }
  if (days === 1) return words.yesterday;
  if (days < 7)
    return capitalize(new Intl.DateTimeFormat(language, { weekday: 'short' }).format(date));
  const sameYear = date.getFullYear() === now.getFullYear();
  const text = new Intl.DateTimeFormat(
    language,
    sameYear
      ? { day: 'numeric', month: 'short' }
      : { day: 'numeric', month: 'short', year: 'numeric' },
  ).format(date);
  return text.replace(/\s?г\.$/u, ''); // Russian appends " г." to a year; the design has none
}
