import type { components } from '@/shared/api/schema';
import { EMPTY_COMPOSE } from './compose-values';
import { textToHtml } from './editor-text';
import type { ReplyStart } from './reply';

export type OpenedDraft = components['schemas']['DraftDto'];

/**
 * What the screen starts with when a draft is opened to be continued (VIN-139): the people, the subject, the text (formatted if the draft has HTML,
 * plain if it has only text), the conversation it belongs to and whether it asks for a read receipt. The draft's files are already among the
 * uploaded ones (the server put them there), so the screen finds them by itself. The signature is not in the text: it is added when the message goes.
 */
export function startOfDraft(draft: OpenedDraft): ReplyStart {
  const rich = draft.html !== null && draft.html.trim() !== '';
  const text = draft.text ?? '';
  return {
    values: {
      ...EMPTY_COMPOSE,
      to: draft.to.map((a) => ({ name: a.name, address: a.address })),
      cc: draft.cc.map((a) => ({ name: a.name, address: a.address })),
      bcc: draft.bcc.map((a) => ({ name: a.name, address: a.address })),
      subject: draft.subject,
    },
    body: rich
      ? { plain: false, html: draft.html ?? '', text }
      : { plain: text.trim() !== '', html: rich ? '' : textToHtml(text), text },
    inReplyTo: draft.inReplyTo,
    references: draft.references.length > 0 ? draft.references : null,
    receipt: draft.requestReceipt,
    draft: { id: draft.id, messageId: draft.messageId },
    attachmentIds: draft.attachments.map((a) => a.id),
  };
}
