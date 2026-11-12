import { describe, expect, it } from 'vitest';
import {
  EMPTY_SELECTION,
  extend,
  keepListed,
  MAX_SELECTED,
  selectAll,
  selectedInOrder,
  toggle,
} from './selection';

const order = ['a', 'b', 'c', 'd', 'e'];
const many = Array.from({ length: 150 }, (_, i) => `m${i}`);

describe('selection of messages', () => {
  it('selects a message and unselects it on a second toggle', () => {
    const one = toggle(EMPTY_SELECTION, 'b').selection;
    expect([...one.ids]).toEqual(['b']);
    expect(one.anchor).toBe('b');
    expect(toggle(one, 'b').selection.ids.size).toBe(0);
  });

  it('selects the range from the last clicked message, in either direction', () => {
    const start = toggle(EMPTY_SELECTION, 'b').selection;
    expect(selectedInOrder(extend(start, order, 'd').selection, order)).toEqual(['b', 'c', 'd']);

    const back = toggle(EMPTY_SELECTION, 'd').selection;
    expect(selectedInOrder(extend(back, order, 'a').selection, order)).toEqual([
      'a',
      'b',
      'c',
      'd',
    ]);
  });

  it('keeps what was selected before when a range is added', () => {
    let s = toggle(EMPTY_SELECTION, 'a').selection;
    s = toggle(s, 'd').selection;
    s = extend(s, order, 'e').selection;
    expect(selectedInOrder(s, order)).toEqual(['a', 'd', 'e']);
  });

  it('treats a range without an earlier click as a single selection', () => {
    expect([...extend(EMPTY_SELECTION, order, 'c').selection.ids]).toEqual(['c']);
  });

  it(`never selects more than ${MAX_SELECTED}, and says when something was left out`, () => {
    const all = selectAll(many);
    expect(all.selection.ids.size).toBe(MAX_SELECTED);
    expect(all.capped).toBe(true);
    expect([...all.selection.ids][0]).toBe('m0');

    const more = toggle(all.selection, 'm120');
    expect(more.capped).toBe(true);
    expect(more.selection.ids.has('m120')).toBe(false);

    const range = extend(toggle(EMPTY_SELECTION, 'm0').selection, many, 'm149');
    expect(range.selection.ids.size).toBe(MAX_SELECTED);
    expect(range.capped).toBe(true);
  });

  it('does not say anything was left out when everything fits', () => {
    expect(selectAll(order).capped).toBe(false);
    expect(selectAll(order).selection.ids.size).toBe(order.length);
  });

  it('forgets the messages that are no longer in the list', () => {
    const s = selectAll(order).selection;
    const kept = keepListed(s, ['a', 'c']);
    expect(selectedInOrder(kept, order)).toEqual(['a', 'c']);
    expect(keepListed(kept, ['a', 'c'])).toBe(kept); // nothing to forget: the same selection
  });
});
