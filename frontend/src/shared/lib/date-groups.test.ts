import { dateGroupLabel, dateGroupOf, groupByDate, type DateGroupWords } from './date-groups';

const words: DateGroupWords = { today: 'Сегодня', yesterday: 'Вчера', week: 'Ранее на неделе' };
// Friday, 9 October 2026, 15:00 local time.
const now = new Date(2026, 9, 9, 15, 0);
const label = (d: Date, lang = 'ru') => dateGroupLabel(dateGroupOf(d, now), now, lang, words);

describe('dateGroupOf', () => {
  it('splits the day at local midnight', () => {
    expect(dateGroupOf(new Date(2026, 9, 9, 0, 0), now).kind).toBe('today');
    expect(dateGroupOf(new Date(2026, 9, 9, 23, 59), now).kind).toBe('today');
    expect(dateGroupOf(new Date(2026, 9, 8, 23, 59), now).kind).toBe('yesterday');
    expect(dateGroupOf(new Date(2026, 9, 8, 0, 0), now).kind).toBe('yesterday');
  });

  it('puts the days 2 to 6 back into "earlier this week" and the 7th into its month', () => {
    expect(dateGroupOf(new Date(2026, 9, 7, 12), now).kind).toBe('week');
    expect(dateGroupOf(new Date(2026, 9, 3, 12), now).kind).toBe('week');
    expect(dateGroupOf(new Date(2026, 9, 2, 12), now)).toEqual({
      kind: 'month',
      year: 2026,
      month: 9,
    });
  });

  it('puts mail from the future into today', () => {
    expect(dateGroupOf(new Date(2026, 9, 12), now).kind).toBe('today');
  });

  it('counts by calendar at the turn of the year', () => {
    const newYear = new Date(2027, 0, 1, 10, 0);
    expect(dateGroupOf(new Date(2026, 11, 31, 23, 0), newYear).kind).toBe('yesterday');
    expect(dateGroupOf(new Date(2026, 11, 28, 12, 0), newYear).kind).toBe('week');
    expect(dateGroupOf(new Date(2026, 11, 20, 12, 0), newYear)).toEqual({
      kind: 'month',
      year: 2026,
      month: 11,
    });
  });
});

describe('dateGroupLabel', () => {
  it('uses the given words for the day groups', () => {
    expect(label(new Date(2026, 9, 9, 8))).toBe('Сегодня');
    expect(label(new Date(2026, 9, 8, 8))).toBe('Вчера');
    expect(label(new Date(2026, 9, 5, 8))).toBe('Ранее на неделе');
  });

  it('names the month, with the year only for earlier years', () => {
    expect(label(new Date(2026, 8, 20)).toLowerCase()).toBe('сентябрь');
    expect(label(new Date(2025, 11, 31)).toLowerCase()).toBe('декабрь 2025');
    expect(label(new Date(2026, 0, 3), 'en')).toBe('January');
    expect(label(new Date(2024, 1, 3), 'en')).toBe('February 2024');
  });
});

describe('groupByDate', () => {
  const at = (...args: [number, number, number, number?]) => ({ date: new Date(...args) });

  it('makes a run for every change of group and keeps the order', () => {
    const items = [
      at(2026, 9, 9, 14),
      at(2026, 9, 9, 8),
      at(2026, 9, 8, 20),
      at(2026, 9, 5),
      at(2026, 8, 30),
      at(2026, 8, 2),
    ];

    const runs = groupByDate(items, (i) => i.date, now);

    expect(runs.map((r) => [r.group.kind, r.items.length])).toEqual([
      ['today', 2],
      ['yesterday', 1],
      ['week', 1],
      ['month', 2],
    ]);
    expect(runs.flatMap((r) => r.items)).toEqual(items);
  });

  it('starts a new run when a group comes back (the list is in arrival order, not in date order)', () => {
    const items = [at(2026, 9, 9), at(2026, 9, 8), at(2026, 9, 9, 1)];

    expect(groupByDate(items, (i) => i.date, now).map((r) => r.group.kind)).toEqual([
      'today',
      'yesterday',
      'today',
    ]);
  });

  it('gives nothing for nothing', () => {
    expect(groupByDate([], () => now, now)).toEqual([]);
  });
});
