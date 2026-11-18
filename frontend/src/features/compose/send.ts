import type { ComposeValues } from './compose-values';
import type { AttachmentItem } from './attachments';
import { isValidAddress } from './recipients';

/** Why the message cannot be sent yet (each has a text for the user: `compose.problem.<name>`). */
export type SendBlocker = 'noRecipients' | 'invalidAddress' | 'uploading' | 'failedFiles';

/**
 * What stands in the way of sending, or null when the message can go: it needs a recipient, every recipient must be an address,
 * and no file may be still on its way or have failed (a file that failed would be silently left out of the message).
 */
export function sendBlocker(values: ComposeValues, files: AttachmentItem[]): SendBlocker | null {
  const everyone = [...values.to, ...values.cc, ...values.bcc];
  if (everyone.length === 0) return 'noRecipients';
  if (everyone.some((r) => !isValidAddress(r.address))) return 'invalidAddress';
  if (files.some((f) => f.status === 'uploading')) return 'uploading';
  if (files.some((f) => f.status === 'failed')) return 'failedFiles';
  return null;
}
