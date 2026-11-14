import type { QueryClient } from '@tanstack/react-query';

/**
 * Echo of the user's own actions (VIN-087). When the user archives or deletes a message, the screen already shows the result
 * (the optimistic update), and a moment later the mail server tells the live channel the same thing ("a message was removed").
 * Reading everything again for that would only make the list blink. So an action that removes messages says so beforehand
 * (`expectRemoval`), and the matching report is swallowed (`consumeRemoval`); anything beyond what was expected is somebody
 * else's doing and is let through.
 */

/** How long an expectation lives: the server's report comes within a second or two; one that never comes must not hide a real removal. */
export const ECHO_EXPIRY_MS = 15_000;

interface Expectation {
  folder: string;
  count: number;
  until: number;
}

const expectations = new WeakMap<QueryClient, Expectation[]>();

/** The action is about to remove `count` messages from `folder`; returns a function that takes the expectation back (the action failed). */
export function expectRemoval(
  client: QueryClient,
  folder: string,
  count: number,
  now: number = Date.now(),
): () => void {
  const entry: Expectation = { folder, count, until: now + ECHO_EXPIRY_MS };
  const list = (expectations.get(client) ?? []).filter((e) => e.until > now);
  list.push(entry);
  expectations.set(client, list);
  return () => {
    const current = expectations.get(client);
    if (current) {
      expectations.set(
        client,
        current.filter((e) => e !== entry),
      );
    }
  };
}

/**
 * The server says `count` messages were removed from `folder`. Returns how many of them nobody here expected (0 = all of them
 * are the echo of our own action, nothing to do).
 */
export function consumeRemoval(
  client: QueryClient,
  folder: string,
  count: number,
  now: number = Date.now(),
): number {
  const list = (expectations.get(client) ?? []).filter((e) => e.until > now);
  let left = count;
  for (const entry of list) {
    if (left === 0) break;
    if (entry.folder !== folder) continue;
    const taken = Math.min(entry.count, left);
    entry.count -= taken;
    left -= taken;
  }
  expectations.set(
    client,
    list.filter((e) => e.count > 0),
  );
  return left;
}
