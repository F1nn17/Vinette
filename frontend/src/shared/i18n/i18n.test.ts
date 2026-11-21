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

  describe('completeness', () => {
    const PLURAL = /_(zero|one|two|few|many|other)$/;
    const base = (key: string) => key.replace(PLURAL, '');

    /** The keys of a dictionary with the plural forms of one text counted as one key. */
    const keysOf = (dictionary: unknown) =>
      [...new Set(entries(dictionary).map(([key]) => base(key)))].sort();

    it('every key of the Russian dictionary is in the English one, and the other way round', () => {
      const russian = new Set(keysOf(ru));
      const english = new Set(keysOf(en));

      expect([...russian].filter((key) => !english.has(key))).toEqual([]);
      expect([...english].filter((key) => !russian.has(key))).toEqual([]);
    });

    it.each(Object.entries(dictionaries))(
      '%s has every plural form its language needs',
      (language, dictionary) => {
        const needed = new Intl.PluralRules(language).resolvedOptions().pluralCategories;
        const forms = new Map<string, Set<string>>();
        for (const [key] of entries(dictionary)) {
          const match = PLURAL.exec(key);
          if (match) forms.set(base(key), (forms.get(base(key)) ?? new Set()).add(match[1]!));
        }

        const missing = [...forms]
          .filter(([, have]) => needed.some((category) => !have.has(category)))
          .map(([key, have]) => `${key}: has ${[...have].join(', ')}, needs ${needed.join(', ')}`);
        expect(missing).toEqual([]);
      },
    );

    it('a key that is a text in one language is not a group of texts in the other', () => {
      const shape = (dictionary: unknown) => {
        const out = new Map<string, 'text' | 'group'>();
        const walk = (value: unknown, path: string) => {
          if (typeof value === 'string') {
            out.set(base(path), 'text');
            return;
          }
          if (path) out.set(path, out.get(path) ?? 'group');
          for (const [key, child] of Object.entries(value as Record<string, unknown>))
            walk(child, path ? `${path}.${key}` : key);
        };
        walk(dictionary, '');
        return out;
      };
      const english = shape(en);
      const clash = [...shape(ru)]
        .filter(([key, kind]) => english.has(key) && english.get(key) !== kind)
        .map(([key]) => key);

      expect(clash).toEqual([]);
    });

    it('the check itself notices a missing key and a missing plural form', () => {
      const withKey = { a: { b: 'x', c: 'y' } };
      const without = { a: { b: 'x' } };

      expect(keysOf(withKey)).not.toEqual(keysOf(without));
      expect(keysOf({ n_one: 'x', n_few: 'y', n_many: 'z', n_other: 'w' })).toEqual(['n']);
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
