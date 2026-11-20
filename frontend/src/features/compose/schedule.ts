/** The soonest the screen lets a message be put off to, from now: a little more than the server's minimum, so that the time is still ahead when it arrives. */
export const MIN_LATER_MINUTES = 2;

/** `YYYY-MM-DDTHH:mm` in local time: the form a `datetime-local` field takes and gives. */
export function toLocalInput(date: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())}T${two(date.getHours())}:${two(date.getMinutes())}`;
}

/** The time a `datetime-local` field holds (local time), or null for an empty or unreadable one. */
export function fromLocalInput(value: string): Date | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** The times offered ready-made: in an hour, tomorrow at 8, and the next Monday at 8 (always after tomorrow's, never today). */
export function presetsFrom(now: Date): { key: 'inHour' | 'tomorrow' | 'monday'; at: Date }[] {
  const inHour = new Date(now.getTime() + 60 * 60_000);
  inHour.setSeconds(0, 0);
  const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 8, 0, 0);
  // Monday is day 1; from Monday itself the next one is a week away, and from Sunday it is tomorrow (tomorrow is then the same time: skip it).
  const untilMonday = (8 - now.getDay()) % 7 || 7;
  const monday = new Date(now.getFullYear(), now.getMonth(), now.getDate() + untilMonday, 8, 0, 0);
  const presets: { key: 'inHour' | 'tomorrow' | 'monday'; at: Date }[] = [
    { key: 'inHour', at: inHour },
    { key: 'tomorrow', at: tomorrow },
  ];
  if (monday.getTime() !== tomorrow.getTime()) presets.push({ key: 'monday', at: monday });
  return presets;
}

/** Whether the time is far enough ahead to be accepted. */
export const isLaterEnough = (at: Date, now: Date) =>
  at.getTime() >= now.getTime() + MIN_LATER_MINUTES * 60_000;

/** How a time is shown to the user: the weekday, the date and the time, in the language of the screen. */
export function formatWhen(at: Date, language: string): string {
  return new Intl.DateTimeFormat(language, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }).format(at);
}
