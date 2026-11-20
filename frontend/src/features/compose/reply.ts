import type { TFunction } from 'i18next';
import type { components } from '@/shared/api/schema';
import type { ComposeValues } from './compose-values';
import { EMPTY_COMPOSE } from './compose-values';
import { textToHtml, type ComposeBody } from './editor-text';
import { withoutDuplicates, type Recipient } from './recipients';

export type Quote = components['schemas']['QuoteDto'];

/** What the new message is: an answer to the sender, an answer to everybody, or the message passed on. */
export type ReplyMode = 'reply' | 'replyAll' | 'forward';

export const REPLY_MODES: readonly ReplyMode[] = ['reply', 'replyAll', 'forward'];

/** Prefixes that mail programs put before a subject when answering or forwarding, in the languages people here use. */
const PREFIX = /^\s*((re|fw|fwd|ответ|отв|пересл|переслано)\s*(\[\d+\])?\s*:\s*)+/i;

/** The subject of the new message: "Re: …" or "Fwd: …", the same prefix never twice, whatever program wrote the old one. */
export function subjectFor(subject: string, mode: ReplyMode): string {
  const base = subject.replace(PREFIX, '').trim();
  const prefix = mode === 'forward' ? 'Fwd:' : 'Re:';
  return base === '' ? prefix : `${prefix} ${base}`;
}

const person = (a: { name: string; address: string }): Recipient => ({
  name: a.name,
  address: a.address,
});

/**
 * Who the new message goes to. An answer goes where the sender asked (Reply-To) or else to the sender; an answer to everybody also
 * takes the other recipients (To, then Cc), never the mailbox itself and nobody twice. When the message was written by the mailbox
 * itself, "answering" it means writing to the people it went to. A forward starts with nobody.
 */
export function recipientsFor(
  quote: Quote,
  mode: ReplyMode,
  own: string,
): Pick<ComposeValues, 'to' | 'cc'> {
  if (mode === 'forward') return { to: [], cc: [] };
  const me = own.toLowerCase();
  const notMe = (r: Recipient) => r.address.toLowerCase() !== me;
  const fromMe = quote.from?.address.toLowerCase() === me;
  const asked = quote.replyTo.map(person);
  const sender = quote.from ? [person(quote.from)] : [];
  const main = withoutDuplicates(
    fromMe ? quote.to.map(person).filter(notMe) : (asked.length > 0 ? asked : sender).filter(notMe),
  );
  if (mode === 'reply') return { to: main, cc: [] };
  const taken = new Set(main.map((r) => r.address.toLowerCase()));
  const others = withoutDuplicates([
    ...(fromMe ? [] : quote.to.map(person)),
    ...quote.cc.map(person),
  ])
    .filter(notMe)
    .filter((r) => !taken.has(r.address.toLowerCase()));
  return { to: main, cc: others };
}

/** The headers that make the new message part of the conversation (an answer only; a forward starts a new one). */
export function threadFor(
  quote: Quote,
  mode: ReplyMode,
): { inReplyTo: string | null; references: string[] | null } {
  if (mode === 'forward' || !quote.messageId) return { inReplyTo: null, references: null };
  return { inReplyTo: quote.messageId, references: [...quote.references, quote.messageId] };
}

const describe = (a: { name: string; address: string } | null | undefined) =>
  a ? (a.name === '' ? a.address : `${a.name} <${a.address}>`) : '';

function formatDate(date: string | null, language: string): string {
  if (!date) return '';
  return new Intl.DateTimeFormat(language, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(date));
}

/** The body of the original as HTML for the editor: its cleaned HTML, or its text as paragraphs. */
const sourceHtml = (quote: Quote) =>
  quote.html && quote.html.trim() !== '' ? quote.html : textToHtml(quote.text ?? '');

/**
 * The body the new message starts with. An answer: an empty line to write in, a line that says who wrote when, and the original quoted
 * (a block with a bar on its left, DESIGN.md §6). A forward: an empty line and the original's headers above the original itself. The
 * words come from the user's language; what the sender wrote is only ever escaped text or cleaned HTML, and the editor keeps only what
 * its toolbar can make of it.
 */
export function bodyFor(
  quote: Quote,
  mode: ReplyMode,
  t: TFunction,
  language: string,
): ComposeBody {
  const date = formatDate(quote.date, language);
  const original = sourceHtml(quote);
  const originalText = quote.text ?? '';
  if (mode === 'forward') {
    const lines = [
      t('compose.reply.forwarded'),
      `${t('compose.from')}: ${describe(quote.from)}`,
      date === '' ? '' : `${t('compose.reply.date')}: ${date}`,
      `${t('compose.subject')}: ${quote.subject}`,
      quote.to.length > 0 ? `${t('compose.to')}: ${quote.to.map(describe).join(', ')}` : '',
    ].filter((l) => l !== '');
    return {
      plain: false,
      html: `<p></p>${textToHtml(lines.join('\n'))}${original}`,
      text: `\n\n${lines.join('\n')}\n\n${originalText}`,
    };
  }
  const attribution = t('compose.reply.wrote', { date, sender: describe(quote.from) });
  const quoted = originalText
    .split(/\r?\n/)
    .map((l) => `> ${l}`)
    .join('\n');
  return {
    plain: false,
    html: `<p></p>${textToHtml(attribution)}<blockquote>${original}</blockquote>`,
    text: `\n\n${attribution}\n${quoted}`,
  };
}

/** Everything the new message starts with. */
export interface ReplyStart {
  values: ComposeValues;
  body: ComposeBody;
  inReplyTo: string | null;
  references: string[] | null;
  /** Ask for a read receipt (a draft that was opened remembers it). */
  receipt?: boolean;
  /** The draft the screen was opened from (VIN-139): the autosave replaces it instead of making another. */
  draft?: { id: string; messageId: string | null };
  /** The ids of the draft's files among the uploaded ones, until the list of uploads is read (so that opening a draft is not a change of it). */
  attachmentIds?: string[];
}

export function startOf(
  quote: Quote,
  mode: ReplyMode,
  own: string,
  t: TFunction,
  language: string,
): ReplyStart {
  const { to, cc } = recipientsFor(quote, mode, own);
  return {
    values: { ...EMPTY_COMPOSE, to, cc, subject: subjectFor(quote.subject, mode) },
    body: bodyFor(quote, mode, t, language),
    ...threadFor(quote, mode),
  };
}

/**
 * The start of an answer with what the user has already typed (in the quick reply) put in the empty line above the quote, so that
 * opening the editor loses nothing. The text is only ever escaped.
 */
export function withTypedText(start: ReplyStart, typed: string): ReplyStart {
  if (typed.trim() === '') return start;
  return {
    ...start,
    body: {
      ...start.body,
      html: start.body.html.replace('<p></p>', textToHtml(typed)),
      text: `${typed}${start.body.text}`,
    },
  };
}
