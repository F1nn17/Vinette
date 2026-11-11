import { formatListDate } from './format-list-date';

const words = { yesterday: 'Вчера' };
// Friday, 9 October 2026, 15:00 local time.
const now = new Date(2026, 9, 9, 15, 0);
const fmt = (d: Date, lang = 'ru') => formatListDate(d, now, lang, words);

describe('formatListDate', () => {
  it('shows the time for today, from the first minute of the day to the last', () => {
    expect(fmt(new Date(2026, 9, 9, 14, 32))).toBe('14:32');
    expect(fmt(new Date(2026, 9, 9, 0, 0))).toBe('00:00');
    expect(fmt(new Date(2026, 9, 9, 23, 59))).toBe('23:59');
  });

  it('says "yesterday" for the previous calendar day, even a minute before midnight', () => {
    expect(fmt(new Date(2026, 9, 8, 23, 59))).toBe('Вчера');
    expect(fmt(new Date(2026, 9, 8, 0, 0))).toBe('Вчера');
  });

  it('shows the weekday for the last six days and the date from the seventh', () => {
    expect(fmt(new Date(2026, 9, 7, 12))).toBe('Ср');
    expect(fmt(new Date(2026, 9, 3, 12))).toBe('Сб'); // 6 days ago
    expect(fmt(new Date(2026, 9, 2, 12))).toBe('2 окт.'); // 7 days ago
    expect(fmt(new Date(2026, 9, 7, 12), 'en')).toBe('Wed');
  });

  it('shows day and month earlier this year, and the year too in earlier years', () => {
    expect(fmt(new Date(2026, 0, 1, 0, 0))).toBe('1 янв.');
    expect(fmt(new Date(2025, 11, 31, 23, 59))).toBe('31 дек. 2025');
    expect(fmt(new Date(2025, 9, 2), 'en')).toBe('Oct 2, 2025');
  });

  it('treats the turn of the year by calendar, not by 365 days', () => {
    const newYear = new Date(2027, 0, 1, 10, 0);
    expect(formatListDate(new Date(2026, 11, 31, 22, 0), newYear, 'ru', words)).toBe('Вчера');
    expect(formatListDate(new Date(2026, 11, 20, 22, 0), newYear, 'ru', words)).toBe(
      '20 дек. 2026',
    );
  });

  it('shows a time for a message dated in the future', () => {
    expect(fmt(new Date(2026, 9, 10, 9, 5))).toBe('09:05');
  });
});
