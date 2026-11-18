import type { Recipient } from './recipients';

/** What the form holds so far. */
export interface ComposeValues {
  to: Recipient[];
  cc: Recipient[];
  bcc: Recipient[];
  subject: string;
}

export const EMPTY_COMPOSE: ComposeValues = { to: [], cc: [], bcc: [], subject: '' };
