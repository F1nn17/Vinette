import en from './en.json';
import ru from './ru.json';

/** Every string of a dictionary with its key path, e.g. "login.errors.unknown". */
function entries(value: unknown, path = ''): [string, string][] {
  if (typeof value === 'string') return [[path, value]];
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) =>
    entries(child, path ? `${path}.${key}` : key),
  );
}

/**
 * Technical vocabulary that must not reach users (docs/design/DESIGN.md section 10): protocols, mail-authentication
 * jargon, network terms. It belongs to the future admin panel only.
 */
// \b does not understand Cyrillic in JavaScript, so word edges are spelled out with Unicode classes.
const BANNED = new RegExp(
  String.raw`(?<![\p{L}\p{N}])(?:imap|smtp|pop3?|dkim|spf|dmarc|tls|ssl|uid|mime|dns|http\p{L}*|api|порт\p{L}*|сервер\p{L}*|протокол\p{L}*|сертификат\p{L}*|server\p{L}*|port|protocol\p{L}*|certificate\p{L}*|token\p{L}*)(?![\p{L}\p{N}])`,
  'iu',
);

const dictionaries = { ru, en } as const;

describe('interface texts', () => {
  describe.each(Object.entries(dictionaries))('%s', (_language, dictionary) => {
    it('contain no technical vocabulary', () => {
      const offenders = entries(dictionary).filter(([, text]) => BANNED.test(text));

      expect(offenders).toEqual([]);
    });

    it('have no empty strings', () => {
      expect(entries(dictionary).filter(([, text]) => text.trim() === '')).toEqual([]);
    });
  });

  it('use the same placeholders in every language', () => {
    const placeholders = (text: string) =>
      [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1]).sort();
    const english = new Map(entries(en));

    for (const [key, text] of entries(ru)) {
      const other = english.get(key);
      if (other !== undefined) {
        expect(placeholders(other), key).toEqual(placeholders(text));
      }
    }
  });

  it('the banned list catches what it is meant to catch', () => {
    for (const word of [
      'IMAP-сервер',
      'порт 993',
      'проверка DKIM',
      'TLS',
      'the server is down',
      'HTTP error',
    ]) {
      expect(BANNED.test(word), word).toBe(true);
    }
    for (const word of [
      'Почта временно недоступна.',
      'Mail is temporarily unavailable.',
      'аэропорт и импорт', // a banned stem inside another word is fine
    ]) {
      expect(BANNED.test(word), word).toBe(false);
    }
  });
});
