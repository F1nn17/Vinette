import { addRecipients, isValidAddress, parseRecipients, recipientLabel } from './recipients';

describe('isValidAddress', () => {
  it.each(['a@x.test', 'anna.s+tag@studio-north.ru', 'user@localhost'])('accepts %s', (a) => {
    expect(isValidAddress(a)).toBe(true);
  });

  it.each([
    '',
    'anna',
    '@x.test',
    'a@',
    'a b@x.test',
    'a@x@y',
    'Anna <a@x.test>',
    'a@x.test,b@x.test',
    'a"b@x.test',
  ])('refuses %j', (a) => {
    expect(isValidAddress(a)).toBe(false);
  });

  it('refuses an address longer than mail allows', () => {
    expect(isValidAddress(`${'a'.repeat(250)}@x.test`)).toBe(false);
  });
});

describe('parseRecipients', () => {
  it('reads a bare address', () => {
    expect(parseRecipients(' a@x.test ')).toEqual([{ name: '', address: 'a@x.test' }]);
  });

  it('reads a name with an address, quoted or not', () => {
    expect(parseRecipients('Anna Smirnova <a@x.test>, "Kovalev, Dmitry" <d@x.test>')).toEqual([
      { name: 'Anna Smirnova', address: 'a@x.test' },
      { name: 'Kovalev, Dmitry', address: 'd@x.test' },
    ]);
  });

  it('splits at commas, semicolons, line breaks and spaces between bare addresses', () => {
    expect(
      parseRecipients('a@x.test;b@x.test\r\nc@x.test, d@x.test e@x.test').map((r) => r.address),
    ).toEqual(['a@x.test', 'b@x.test', 'c@x.test', 'd@x.test', 'e@x.test']);
  });

  it('keeps what is not an address, so that it can be shown as a mistake', () => {
    expect(parseRecipients('anna, b@x.test')).toEqual([
      { name: '', address: 'anna' },
      { name: '', address: 'b@x.test' },
    ]);
  });

  it('counts the same address in another case as one person, keeping the first', () => {
    expect(parseRecipients('Anna <A@x.test>, a@X.test')).toEqual([
      { name: 'Anna', address: 'A@x.test' },
    ]);
  });

  it('gives nothing for blank text', () => {
    expect(parseRecipients(' ,; \n')).toEqual([]);
  });
});

describe('addRecipients and recipientLabel', () => {
  it('skips an address the list already has', () => {
    const list = [{ name: 'Anna', address: 'a@x.test' }];

    expect(
      addRecipients(list, [
        { name: '', address: 'A@x.test' },
        { name: '', address: 'b@x.test' },
      ]),
    ).toEqual([...list, { name: '', address: 'b@x.test' }]);
  });

  it('shows the name, or the address without one', () => {
    expect(recipientLabel({ name: 'Anna', address: 'a@x.test' })).toBe('Anna');
    expect(recipientLabel({ name: '', address: 'a@x.test' })).toBe('a@x.test');
  });
});
