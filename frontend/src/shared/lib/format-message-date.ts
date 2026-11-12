const capitalize = (text: string) => text.charAt(0).toLocaleUpperCase() + text.slice(1);

/**
 * The date of an open message (DESIGN.md §5): "Fri, 2 Oct, 14:32"; in an earlier year the year is added. Local time of the
 * reader.
 */
export function formatMessageDate(date: Date, now: Date, language: string): string {
  const sameYear = date.getFullYear() === now.getFullYear();
  const text = new Intl.DateTimeFormat(language, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    ...(sameYear ? {} : { year: 'numeric' }),
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
  return capitalize(text.replace(/\s?г\.(?=,)/u, ''));
}
