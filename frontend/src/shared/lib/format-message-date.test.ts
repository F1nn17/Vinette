import { formatMessageDate } from './format-message-date';

// Friday, 9 October 2026, 15:00 local time.
const now = new Date(2026, 9, 9, 15, 0);

describe('formatMessageDate', () => {
  it('writes weekday, day, month and time, with a capital letter', () => {
    expect(formatMessageDate(new Date(2026, 9, 2, 14, 32), now, 'ru')).toBe('Пт, 2 окт., 14:32');
  });

  it('adds the year for an earlier year, without the Russian "г."', () => {
    expect(formatMessageDate(new Date(2025, 11, 31, 23, 59), now, 'ru')).toBe(
      'Ср, 31 дек. 2025, 23:59',
    );
  });

  it('follows the language', () => {
    expect(formatMessageDate(new Date(2026, 9, 2, 14, 32), now, 'en')).toMatch(
      /^Fri, (Oct 2|2 Oct), (14:32|02:32 PM)$/u,
    );
  });

  it('keeps the midnight borders of the local day', () => {
    expect(formatMessageDate(new Date(2026, 0, 1, 0, 0), now, 'ru')).toBe('Чт, 1 янв., 00:00');
  });
});
