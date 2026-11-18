/** What the body of the message holds. */
export interface ComposeBody {
  /** Plain text mode: no formatting, the message goes as text only. */
  plain: boolean;
  /** The formatted body; empty when there is nothing. Meaningful in rich mode. */
  html: string;
  /** The same body as text: what the plain text field holds, and what a plain message goes as. */
  text: string;
}

export const EMPTY_BODY: ComposeBody = { plain: false, html: '', text: '' };

const ESCAPES: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' };

/** Text as HTML paragraphs, one for each line; nothing typed is taken for markup. */
export function textToHtml(text: string): string {
  if (text.trim() === '') return '';
  return text
    .replace(/\r\n?/g, '\n')
    .split('\n')
    .map((line) => `<p>${line.replace(/[&<>"]/g, (c) => ESCAPES[c] ?? c)}</p>`)
    .join('');
}

/** The only kinds of address a link in a message may have. */
const LINK_SCHEME = /^(https?|mailto):/i;

/**
 * The address a link typed by the user leads to, or null when it is not one a message may carry: `http`, `https` and `mailto` only (never
 * `javascript:` and the like). A bare `example.org/page` is taken as `https://example.org/page`, and a bare `a@x.test` as `mailto:`.
 */
export function normalizeLink(typed: string): string | null {
  const text = typed.trim();
  if (text === '' || /\s/.test(text)) return null;
  if (LINK_SCHEME.test(text)) return text;
  if (/^[a-z][a-z0-9+.-]*:/i.test(text) && !/^[^/]*\.[^/]*:\d+/.test(text)) return null; // another scheme
  if (/^[^@/]+@[^@/]+$/.test(text)) return `mailto:${text}`;
  return `https://${text}`;
}
