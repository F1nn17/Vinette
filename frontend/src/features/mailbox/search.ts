import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';

/** The query is kept in the address as `?q=`, so a search survives a reload and the Back button. */
export const SEARCH_PARAM = 'q';

/** The server takes up to 8 words and 200 characters (docs/architecture.md, "Поиск"). */
export const MAX_WORDS = 8;
export const MAX_QUERY_LENGTH = 200;

/** How long the box waits after the last key before it searches. */
export const SEARCH_DELAY_MS = 350;

/** What is sent to the server for what was typed: the words trimmed and cut to what the server takes; empty if there is none. */
export function queryOf(typed: string): string {
  return typed
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, MAX_WORDS)
    .join(' ')
    .slice(0, MAX_QUERY_LENGTH);
}

export interface SearchBox {
  /** What is in the box now (it can be ahead of `query` by the delay). */
  typed: string;
  /** What the list is searched for now (from the address); empty for no search. */
  query: string;
  onType: (value: string) => void;
  /** Search at once, without waiting for the delay (Enter). */
  commit: () => void;
  /** Empty the box and show the whole folder again. */
  clear: () => void;
}

/**
 * The search box of the list (VIN-111): the text goes to the address after a short pause (or at once with Enter), so the list asks the
 * server once for what the person meant, not for every letter. The address is the truth: another folder or the Back button changes the box.
 */
export function useSearchBox(): SearchBox {
  const [params, setParams] = useSearchParams();
  const query = queryOf(params.get(SEARCH_PARAM) ?? '');
  const [typed, setTyped] = useState(params.get(SEARCH_PARAM) ?? '');

  const write = (value: string) => {
    const next = queryOf(value);
    if (next === queryOf(params.get(SEARCH_PARAM) ?? '')) return;
    const copy = new URLSearchParams(params);
    if (next) copy.set(SEARCH_PARAM, next);
    else copy.delete(SEARCH_PARAM);
    setParams(copy, { replace: true });
  };

  // The address changed from outside (another folder, Back): the box follows it.
  const [followed, setFollowed] = useState(query);
  if (followed !== query) {
    setFollowed(query);
    if (queryOf(typed) !== query) setTyped(query);
  }

  useEffect(() => {
    if (queryOf(typed) === query) return;
    const timer = setTimeout(() => write(typed), SEARCH_DELAY_MS);
    return () => clearTimeout(timer);
    // `write` only reads the address, which `query` stands for.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [typed, query]);

  return {
    typed,
    query,
    onType: (value) => setTyped(value.slice(0, MAX_QUERY_LENGTH)),
    commit: () => write(typed),
    clear: () => {
      setTyped('');
      write('');
    },
  };
}
