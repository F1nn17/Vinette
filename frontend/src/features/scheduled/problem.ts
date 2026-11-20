import type { TFunction } from 'i18next';

/** The codes of the API that say why a scheduled message did not go; any other is told in general words. */
const KNOWN = new Set([
  'recipient_rejected',
  'send_refused',
  'send_too_large',
  'send_uncertain',
  'mail_unavailable',
  'session_revoked',
]);

/** Why a scheduled message did not go, in words for the user (no codes, no server talk). */
export function problemText(problem: string | null | undefined, t: TFunction): string {
  return t(`scheduled.problem.${problem && KNOWN.has(problem) ? problem : 'other'}`);
}
