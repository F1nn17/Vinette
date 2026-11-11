import { calendarDaysAgo } from './format-list-date';

/** A group of the message list: today, yesterday, the rest of the week, then one group per month. */
export type DateGroup =
  | { kind: 'today' }
  | { kind: 'yesterday' }
  | { kind: 'week' }
  | { kind: 'month'; year: number; month: number };

/** The group of a date, by the same day borders as `formatListDate`: days 2 to 6 back are "earlier this week". */
export function dateGroupOf(date: Date, now: Date): DateGroup {
  const days = calendarDaysAgo(date, now);
  if (days <= 0) return { kind: 'today' };
  if (days === 1) return { kind: 'yesterday' };
  if (days < 7) return { kind: 'week' };
  return { kind: 'month', year: date.getFullYear(), month: date.getMonth() };
}

/** A key that is equal for the dates of one group. */
export function dateGroupKey(group: DateGroup): string {
  return group.kind === 'month' ? `m${group.year}-${group.month}` : group.kind;
}

export interface DateGroupWords {
  today: string;
  yesterday: string;
  week: string;
}

/**
 * The heading of a group: "Today", "Yesterday", "Earlier this week", then the month ("October"), with the year for
 * earlier years ("October 2025"). The month comes in the language's own words.
 */
export function dateGroupLabel(
  group: DateGroup,
  now: Date,
  language: string,
  words: DateGroupWords,
): string {
  if (group.kind !== 'month') return words[group.kind];
  const first = new Date(group.year, group.month, 1);
  const text =
    group.year === now.getFullYear()
      ? new Intl.DateTimeFormat(language, { month: 'long' }).format(first)
      : new Intl.DateTimeFormat(language, { month: 'long', year: 'numeric' }).format(first);
  return text.replace(/\s?г\.$/u, '');
}

/** Splits items that are already in order into runs of the same group; the order is never changed. */
export function groupByDate<T>(
  items: readonly T[],
  dateOf: (item: T) => Date,
  now: Date,
): { group: DateGroup; items: T[] }[] {
  const runs: { group: DateGroup; items: T[] }[] = [];
  for (const item of items) {
    const group = dateGroupOf(dateOf(item), now);
    const last = runs.at(-1);
    if (last && dateGroupKey(last.group) === dateGroupKey(group)) last.items.push(item);
    else runs.push({ group, items: [item] });
  }
  return runs;
}
