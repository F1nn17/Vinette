import { MAX_QUERY_LENGTH, MAX_WORDS, queryOf } from './search';

describe('queryOf', () => {
  it('trims the words and joins them with one space', () => {
    expect(queryOf('  Отчёт   за\tквартал ')).toBe('Отчёт за квартал');
  });

  it('is empty for an empty box or one of spaces', () => {
    expect(queryOf('')).toBe('');
    expect(queryOf('   \t ')).toBe('');
  });

  it('keeps only the words the server takes, and only as many letters', () => {
    const many = Array.from({ length: MAX_WORDS + 3 }, (_, i) => `w${i}`).join(' ');
    expect(queryOf(many).split(' ')).toHaveLength(MAX_WORDS);
    expect(queryOf('я'.repeat(MAX_QUERY_LENGTH + 50))).toHaveLength(MAX_QUERY_LENGTH);
  });
});
