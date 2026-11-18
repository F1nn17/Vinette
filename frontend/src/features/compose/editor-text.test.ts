import { normalizeLink, textToHtml } from './editor-text';

describe('textToHtml', () => {
  it('makes a paragraph of each line, keeping empty lines', () => {
    expect(textToHtml('one\n\ntwo')).toBe('<p>one</p><p></p><p>two</p>');
  });

  it('never takes typed text for markup', () => {
    expect(textToHtml('<img src=x onerror=alert(1)> & "q"')).toBe(
      '<p>&lt;img src=x onerror=alert(1)&gt; &amp; &quot;q&quot;</p>',
    );
  });

  it('gives nothing for blank text', () => {
    expect(textToHtml(' \n ')).toBe('');
  });
});

describe('normalizeLink', () => {
  it.each([
    ['https://example.org/a', 'https://example.org/a'],
    ['HTTP://example.org', 'HTTP://example.org'],
    ['mailto:a@x.test', 'mailto:a@x.test'],
    ['example.org/page', 'https://example.org/page'],
    ['a@x.test', 'mailto:a@x.test'],
    ['example.org:8080/x', 'https://example.org:8080/x'],
    ['  example.org  ', 'https://example.org'],
  ])('turns %j into %j', (typed, href) => {
    expect(normalizeLink(typed)).toBe(href);
  });

  it.each([
    '',
    '   ',
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    'data:text/html,<b>x</b>',
    'vbscript:x',
    'ftp://example.org',
    'tel:+123',
    'two words.org',
  ])('refuses %j', (typed) => {
    expect(normalizeLink(typed)).toBeNull();
  });
});
