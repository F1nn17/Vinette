import { buildDraftContent } from './draft';
import { EMPTY_COMPOSE } from './compose-values';
import { withSignature } from './signature';

const to = [{ name: '', address: 'a@x.test' }];
const rich = (html: string, text = 'x') =>
  buildDraftContent({ ...EMPTY_COMPOSE, to }, { plain: false, html, text }, [])!;
const plain = (text: string) =>
  buildDraftContent({ ...EMPTY_COMPOSE, to }, { plain: true, html: '', text }, [])!;

describe('withSignature', () => {
  it('puts the lines of the signature under a formatted message, after an empty line, as paragraphs', () => {
    const result = withSignature(rich('<p>Hello</p>'), { text: 'Regards,\nAlice' });

    expect(result.html).toBe('<p>Hello</p><p></p><p>Regards,</p><p>Alice</p>');
    expect(result.text).toBeNull();
  });

  it('puts it under a plain message after the delimiter line that mail programs know', () => {
    const result = withSignature(plain('Hello'), { text: 'Regards,\nAlice' });

    expect(result.text).toBe('Hello\n\n-- \nRegards,\nAlice');
    expect(result.html).toBeNull();
  });

  it('still signs a message whose body is empty', () => {
    expect(
      withSignature(
        buildDraftContent({ ...EMPTY_COMPOSE, to }, { plain: false, html: '', text: '' }, [])!,
        { text: 'Alice' },
      ).html,
    ).toBe('<p></p><p>Alice</p>');
    expect(withSignature(plain(''), { text: 'Alice' }).text).toBe('\n\n-- \nAlice');
  });

  it('changes nothing without a signature or with a blank one', () => {
    const content = rich('<p>Hello</p>');

    expect(withSignature(content, null)).toBe(content);
    expect(withSignature(content, { text: '  \n ' })).toBe(content);
  });

  it('never takes the text of the signature for markup, and does not change what it is given', () => {
    const content = rich('<p>Hello</p>');

    const result = withSignature(content, { text: '<img src=x onerror=alert(1)>' });

    expect(result.html).not.toContain('<img');
    expect(result.html).toContain('&lt;img');
    expect(content.html).toBe('<p>Hello</p>');
  });

  it('is applied once to what it is given: a second signature is a second call, never part of the first', () => {
    const once = withSignature(rich('<p>Hello</p>'), { text: 'Alice' });

    expect(once.html?.split('Alice').length).toBe(2);
  });
});
