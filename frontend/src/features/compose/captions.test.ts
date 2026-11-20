import type { TFunction } from 'i18next';
import { lastMailCaption } from './captions';

const t = ((key: string, options?: Record<string, string>) =>
  options ? `${key}|${Object.values(options).join('|')}` : key) as unknown as TFunction;

// Local noon, so that the day boundaries do not depend on the zone of the machine.
const at = (y: number, m: number, d: number, h = 12) => new Date(y, m - 1, d, h, 0, 0);

describe('lastMailCaption', () => {
  const now = at(2026, 11, 18, 15);

  it('says nothing until the answer is in', () => {
    expect(lastMailCaption(undefined, now, 'ru', t)).toBe('');
  });

  it('says the person is not in the address book when the mailbox has no mail with them', () => {
    expect(lastMailCaption(null, now, 'ru', t)).toBe('compose.people.unknown');
  });

  it('says today, whatever the hour of the day', () => {
    expect(lastMailCaption(at(2026, 11, 18, 0).toISOString(), now, 'ru', t)).toBe(
      'compose.people.lastToday',
    );
    expect(lastMailCaption(at(2026, 11, 18, 23).toISOString(), now, 'ru', t)).toBe(
      'compose.people.lastToday',
    );
  });

  it('says yesterday for the day before, counted by the calendar and not by 24 hours', () => {
    expect(lastMailCaption(at(2026, 11, 17, 23).toISOString(), now, 'ru', t)).toBe(
      'compose.people.lastYesterday',
    );
    expect(lastMailCaption(at(2026, 11, 17, 0).toISOString(), now, 'ru', t)).toBe(
      'compose.people.lastYesterday',
    );
  });

  it('gives the date for anything older, with the year only when it is not this year', () => {
    const thisYear = lastMailCaption(at(2026, 10, 2).toISOString(), now, 'en', t);
    const earlier = lastMailCaption(at(2025, 10, 2).toISOString(), now, 'en', t);

    expect(thisYear).toBe('compose.people.lastOn|Oct 2');
    expect(earlier).toBe('compose.people.lastOn|Oct 2, 2025');
  });

  it('writes the date in the language of the screen', () => {
    expect(lastMailCaption(at(2026, 10, 2).toISOString(), now, 'ru', t)).toMatch(
      /^compose\.people\.lastOn\|2 окт/,
    );
  });
});
