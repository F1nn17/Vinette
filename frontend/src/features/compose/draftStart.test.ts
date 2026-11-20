import { startOfDraft, type OpenedDraft } from './draftStart';

const draft = (patch: Partial<OpenedDraft> = {}): OpenedDraft => ({
  id: 'RHJhZnRz.3.12',
  messageId: 'made@vinette.test',
  from: { name: '', address: 'me@vinette.test' },
  to: [{ name: 'Анна', address: 'anna@example.org' }],
  cc: [{ name: '', address: 'carol@example.org' }],
  bcc: [{ name: '', address: 'hidden@example.org' }],
  subject: 'Смета',
  text: 'Привет',
  html: '<p>Привет</p>',
  attachments: [],
  inReplyTo: 'm3@example.org',
  references: ['m1@example.org', 'm3@example.org'],
  date: '2026-11-17T09:00:00Z',
  requestReceipt: false,
  ...patch,
});

describe('startOfDraft', () => {
  it('brings back the people, the subject, the conversation and which draft it is', () => {
    const start = startOfDraft(draft());

    expect(start.values).toEqual({
      to: [{ name: 'Анна', address: 'anna@example.org' }],
      cc: [{ name: '', address: 'carol@example.org' }],
      bcc: [{ name: '', address: 'hidden@example.org' }],
      subject: 'Смета',
    });
    expect([start.inReplyTo, start.references]).toEqual([
      'm3@example.org',
      ['m1@example.org', 'm3@example.org'],
    ]);
    expect(start.draft).toEqual({ id: 'RHJhZnRz.3.12', messageId: 'made@vinette.test' });
  });

  it('keeps formatted text formatted', () => {
    expect(startOfDraft(draft()).body).toEqual({
      plain: false,
      html: '<p>Привет</p>',
      text: 'Привет',
    });
  });

  it('opens a draft that has only text as plain text, as it was written', () => {
    const body = startOfDraft(draft({ html: null, text: 'one\ntwo' })).body;

    expect(body).toEqual({ plain: true, html: '<p>one</p><p>two</p>', text: 'one\ntwo' });
  });

  it('opens a draft with no body as an empty formatted one', () => {
    expect(startOfDraft(draft({ html: null, text: null })).body).toEqual({
      plain: false,
      html: '',
      text: '',
    });
  });

  it('remembers the request for a read receipt, and has no thread when there is none', () => {
    const start = startOfDraft(draft({ requestReceipt: true, inReplyTo: null, references: [] }));

    expect([start.receipt, start.inReplyTo, start.references]).toEqual([true, null, null]);
  });

  it('never takes the text of a draft for markup', () => {
    const body = startOfDraft(draft({ html: null, text: '<script>alert(1)</script>' })).body;

    expect(body.html).toBe('<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>');
  });
});
