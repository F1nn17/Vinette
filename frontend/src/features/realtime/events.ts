import type { components } from '@/shared/api/schema';

type Schemas = components['schemas'];

/**
 * What the server sends over the real-time channel (the client method `mailEvent`). The shapes are generated; only the
 * `type` is narrowed here, so that the events can be told apart.
 */
export type MailEvent =
  | (Schemas['ArrivedEventDto'] & { type: 'arrived' })
  | (Schemas['RemovedEventDto'] & { type: 'removed' })
  | (Schemas['MarksEventDto'] & { type: 'marks' })
  | (Schemas['CountersEventDto'] & { type: 'counters' })
  | (Schemas['StaleEventDto'] & { type: 'stale' });

/** The name of the client method the server calls. */
export const EVENT_METHOD = 'mailEvent';

const KNOWN = new Set(['arrived', 'removed', 'marks', 'counters', 'stale']);

/** An event of a kind this client knows, or null (an older client meeting a newer server ignores what it cannot read). */
export function asMailEvent(value: unknown): MailEvent | null {
  if (typeof value !== 'object' || value === null) return null;
  const { type, folder } = value as { type?: unknown; folder?: unknown };
  return typeof type === 'string' && KNOWN.has(type) && typeof folder === 'string'
    ? (value as MailEvent)
    : null;
}
