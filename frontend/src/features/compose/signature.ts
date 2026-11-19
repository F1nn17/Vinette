import { textToHtml } from './editor-text';
import type { DraftContent } from './draft';

/** The line that, by old custom (RFC 3676), stands between a plain text message and its signature; mail programs fold what follows it. */
const DELIMITER = '-- ';

/** A signature as the message needs it. */
export interface SignatureText {
  text: string;
}

/**
 * The message with the signature under it. A formatted message gets an empty line and the lines of the signature as paragraphs; a plain
 * one gets the usual delimiter line and the text. The signature is added when the message is sent, never to the draft and never into the
 * editor: it is shown beside the editor, a different one can be chosen at any time, and it can never be twice in a message. The text of a
 * signature is only ever escaped.
 */
export function withSignature(
  content: DraftContent,
  signature: SignatureText | null,
): DraftContent {
  const text = signature?.text.trim() ?? '';
  if (text === '') return content;
  // A message whose body is plain text has `text` and no `html`.
  if (content.text !== null && content.html === null) {
    return { ...content, text: `${content.text}\n\n${DELIMITER}\n${text}` };
  }
  return { ...content, html: htmlWithSignature(content.html ?? '', signature) };
}

/** A formatted body with the signature under it: an empty line, then the lines of the signature as paragraphs. */
export function htmlWithSignature(html: string, signature: SignatureText | null): string {
  const text = signature?.text.trim() ?? '';
  return text === '' ? html : `${html}<p></p>${textToHtml(text)}`;
}
