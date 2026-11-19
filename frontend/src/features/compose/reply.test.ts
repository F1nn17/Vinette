import type { TFunction } from 'i18next';
import {
  bodyFor,
  recipientsFor,
  startOf,
  subjectFor,
  threadFor,
  withTypedText,
  type Quote,
} from './reply';

// Shows the key and the values it was given: the words themselves are the dictionary's.
const t = ((key: string, options?: Record<string, string>) =>
  options ? `${key}|${Object.values(options).join('|')}` : key) as unknown as TFunction;

const anna = { name: 'Анна Смирнова', address: 'anna@example.org' };
const me = 'me@vinette.test';

const quote = (patch: Partial<Quote> = {}): Quote => ({
  messageId: 'm3@example.org',
  references: ['m1@example.org', 'm2@example.org'],
  from: anna,
  replyTo: [],
  to: [
    { name: '', address: me },
    { name: 'Boris', address: 'boris@example.org' },
  ],
  cc: [{ name: '', address: 'carol@example.org' }],
  date: '2026-11-17T09:00:00Z',
  subject: 'Смета',
  text: 'Привет\nСмотри файл',
  html: null,
  attachments: [],
  ...patch,
});

describe('subjectFor', () => {
  it.each([
    ['Смета', 'reply', 'Re: Смета'],
    ['Re: Смета', 'reply', 'Re: Смета'],
    ['RE: Re: Смета', 'reply', 'Re: Смета'],
    ['Ответ: Смета', 'reply', 'Re: Смета'],
    ['Fwd: Смета', 'reply', 'Re: Смета'],
    ['Смета', 'forward', 'Fwd: Смета'],
    ['Re: Смета', 'forward', 'Fwd: Смета'],
    ['FW: Fwd: Смета', 'forward', 'Fwd: Смета'],
    ['Пересл: Смета', 'forward', 'Fwd: Смета'],
    ['Re[2]: Смета', 'reply', 'Re: Смета'],
    ['', 'reply', 'Re:'],
    ['Reply about Re: things', 'reply', 'Re: Reply about Re: things'],
  ] as const)('%j (%s) becomes %j', (subject, mode, expected) => {
    expect(subjectFor(subject, mode)).toBe(expected);
  });
});

describe('recipientsFor', () => {
  it('answers the sender, or where the sender asked answers to go', () => {
    expect(recipientsFor(quote(), 'reply', me)).toEqual({ to: [anna], cc: [] });
    const elsewhere = { name: 'Личный', address: 'anna.private@example.org' };
    expect(recipientsFor(quote({ replyTo: [elsewhere] }), 'reply', me)).toEqual({
      to: [elsewhere],
      cc: [],
    });
  });

  it('answers everybody: the other recipients too, never the mailbox itself and nobody twice', () => {
    const result = recipientsFor(
      quote({
        to: [
          { name: '', address: me },
          { name: '', address: 'boris@example.org' },
          { name: '', address: 'ANNA@example.org' },
        ],
        cc: [
          { name: '', address: 'carol@example.org' },
          { name: '', address: 'Boris@example.org' },
          { name: '', address: me.toUpperCase() },
        ],
      }),
      'replyAll',
      me,
    );

    expect(result.to).toEqual([anna]);
    expect(result.cc.map((r) => r.address)).toEqual(['boris@example.org', 'carol@example.org']);
  });

  it('writes to the people a message of the mailbox itself went to', () => {
    const mine = quote({ from: { name: '', address: me } });

    expect(recipientsFor(mine, 'reply', me).to.map((r) => r.address)).toEqual([
      'boris@example.org',
    ]);
    const all = recipientsFor(mine, 'replyAll', me);
    expect(all.to.map((r) => r.address)).toEqual(['boris@example.org']);
    expect(all.cc.map((r) => r.address)).toEqual(['carol@example.org']);
  });

  it('starts a forward with nobody', () => {
    expect(recipientsFor(quote(), 'forward', me)).toEqual({ to: [], cc: [] });
  });
});

describe('threadFor', () => {
  it('refers to the message and carries the conversation so far, oldest first, the message last', () => {
    expect(threadFor(quote(), 'reply')).toEqual({
      inReplyTo: 'm3@example.org',
      references: ['m1@example.org', 'm2@example.org', 'm3@example.org'],
    });
    expect(threadFor(quote(), 'replyAll').inReplyTo).toBe('m3@example.org');
  });

  it('starts no conversation for a forward, and cannot refer to a message that has no id', () => {
    expect(threadFor(quote(), 'forward')).toEqual({ inReplyTo: null, references: null });
    expect(threadFor(quote({ messageId: null }), 'reply')).toEqual({
      inReplyTo: null,
      references: null,
    });
  });
});

describe('bodyFor', () => {
  it('leaves an empty line to write in, then who wrote when, then the original quoted', () => {
    const body = bodyFor(quote(), 'reply', t, 'ru');

    expect(body.html.startsWith('<p></p><p>compose.reply.wrote|')).toBe(true);
    expect(body.html).toContain('Анна Смирнова &lt;anna@example.org&gt;');
    expect(body.html).toContain('<blockquote><p>Привет</p><p>Смотри файл</p></blockquote>');
    expect(body.plain).toBe(false);
    expect(body.text).toContain('> Привет\n> Смотри файл');
  });

  it('quotes the cleaned html of the original when there is some', () => {
    const body = bodyFor(quote({ html: '<p>Hello <b>you</b></p>' }), 'reply', t, 'en');

    expect(body.html).toContain('<blockquote><p>Hello <b>you</b></p></blockquote>');
  });

  it('never takes what the sender wrote or how the sender is called for markup', () => {
    const body = bodyFor(
      quote({
        from: { name: '<img src=x onerror=alert(1)>', address: 'a@x.test' },
        text: '<script>x</script>',
      }),
      'reply',
      t,
      'en',
    );

    expect(body.html).not.toContain('<img');
    expect(body.html).not.toContain('<script');
    expect(body.html).toContain('&lt;script&gt;');
  });

  it('puts the headers of the original above it for a forward, without a quote bar', () => {
    const body = bodyFor(quote(), 'forward', t, 'ru');

    expect(body.html.startsWith('<p></p><p>compose.reply.forwarded</p>')).toBe(true);
    expect(body.html).toContain('compose.from: Анна Смирнова &lt;anna@example.org&gt;');
    expect(body.html).toContain('compose.subject: Смета');
    expect(body.html).toContain('compose.to: me@vinette.test, Boris &lt;boris@example.org&gt;');
    expect(body.html).not.toContain('<blockquote>');
    expect(body.html.endsWith('<p>Привет</p><p>Смотри файл</p>')).toBe(true);
  });
});

describe('startOf', () => {
  it('puts it all together for an answer to everybody', () => {
    const start = startOf(quote(), 'replyAll', me, t, 'ru');

    expect(start.values.subject).toBe('Re: Смета');
    expect(start.values.to).toEqual([anna]);
    expect(start.values.cc.map((r) => r.address)).toEqual([
      'boris@example.org',
      'carol@example.org',
    ]);
    expect(start.values.bcc).toEqual([]);
    expect(start.inReplyTo).toBe('m3@example.org');
    expect(start.references).toHaveLength(3);
  });
});

describe('withTypedText', () => {
  it('puts what was typed in the empty line above the quote, as escaped paragraphs', () => {
    const start = startOf(quote(), 'reply', me, t, 'en');

    const typed = withTypedText(start, 'One <i>line</i>\nTwo');

    expect(
      typed.body.html.startsWith(
        '<p>One &lt;i&gt;line&lt;/i&gt;</p><p>Two</p><p>compose.reply.wrote',
      ),
    ).toBe(true);
    expect(typed.body.html).toContain('<blockquote>');
    expect(typed.body.text.startsWith('One <i>line</i>\nTwo')).toBe(true);
    expect(typed.values).toEqual(start.values);
  });

  it('changes nothing for blank text', () => {
    const start = startOf(quote(), 'reply', me, t, 'en');

    expect(withTypedText(start, '  \n ')).toBe(start);
  });
});
