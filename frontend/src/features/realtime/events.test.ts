import { asMailEvent } from './events';

describe('asMailEvent', () => {
  it.each(['arrived', 'removed', 'marks', 'counters', 'stale'])(
    'reads an event of the kind "%s"',
    (type) => {
      const value = { type, folder: 'INBOX' };

      expect(asMailEvent(value)).toBe(value);
    },
  );

  it.each([
    ['a kind this client does not know', { type: 'newfangled', folder: 'INBOX' }],
    ['no folder', { type: 'stale' }],
    ['a folder that is not text', { type: 'stale', folder: 3 }],
    ['no kind', { folder: 'INBOX' }],
    ['text', 'arrived'],
    ['nothing', null],
  ])('ignores %s', (_what, value) => {
    expect(asMailEvent(value)).toBeNull();
  });
});
