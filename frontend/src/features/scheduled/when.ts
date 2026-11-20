/** The start of the calendar day of a date, in local time. */
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

const time = (at: Date, language: string) =>
  new Intl.DateTimeFormat(language, { hour: '2-digit', minute: '2-digit' }).format(at);

/**
 * A short time for a row of the list: "today, 08:00", "tomorrow, 08:00", the weekday and time for the coming week ("Mon, 08:00"), the
 * date and time beyond it. The words "today" and "tomorrow" are the caller's, from the dictionary.
 */
export function shortWhen(
  at: Date,
  now: Date,
  language: string,
  words: { today: string; tomorrow: string },
): string {
  const days = Math.round((startOfDay(at) - startOfDay(now)) / 86_400_000);
  if (days <= 0) return `${words.today}, ${time(at, language)}`;
  if (days === 1) return `${words.tomorrow}, ${time(at, language)}`;
  if (days < 7) {
    return `${new Intl.DateTimeFormat(language, { weekday: 'short' }).format(at)}, ${time(at, language)}`;
  }
  return new Intl.DateTimeFormat(language, {
    day: 'numeric',
    month: 'short',
    ...(at.getFullYear() === now.getFullYear() ? {} : { year: 'numeric' }),
    hour: '2-digit',
    minute: '2-digit',
  }).format(at);
}

/** The full time for the card: the weekday, the date and the time. */
export function fullWhen(at: Date, language: string): string {
  return new Intl.DateTimeFormat(language, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }).format(at);
}
