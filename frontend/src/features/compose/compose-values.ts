/** What the form holds so far. Recipients are plain text until the recipient chips arrive (VIN-097). */
export interface ComposeValues {
  to: string;
  cc: string;
  bcc: string;
  subject: string;
}

export const EMPTY_COMPOSE: ComposeValues = { to: '', cc: '', bcc: '', subject: '' };
