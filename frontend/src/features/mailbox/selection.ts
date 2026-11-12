/** The most messages that can be selected: one batch action takes at most this many (the server's limit too). */
export const MAX_SELECTED = 100;

/** The selected messages of the list, and the row a Shift range starts from. */
export interface Selection {
  readonly ids: ReadonlySet<string>;
  readonly anchor: string | null;
}

/** What a change of the selection gives: the new selection, and whether some messages were left out by the limit. */
export interface SelectionChange {
  selection: Selection;
  capped: boolean;
}

export const EMPTY_SELECTION: Selection = { ids: new Set(), anchor: null };

/** Selects a message, or unselects it if it was selected (a click on its box, or Ctrl/⌘ and a click on the row). */
export function toggle(selection: Selection, id: string): SelectionChange {
  const ids = new Set(selection.ids);
  if (ids.has(id)) {
    ids.delete(id);
    return { selection: { ids, anchor: id }, capped: false };
  }
  if (ids.size >= MAX_SELECTED) return { selection, capped: true };
  ids.add(id);
  return { selection: { ids, anchor: id }, capped: false };
}

/**
 * Shift and a click: selects every message between the last one clicked and this one, in the order of the list (both
 * included). Without a previous click it selects just this one. What is over the limit is left out.
 */
export function extend(
  selection: Selection,
  order: readonly string[],
  id: string,
): SelectionChange {
  const from = selection.anchor === null ? -1 : order.indexOf(selection.anchor);
  const to = order.indexOf(id);
  if (to < 0) return { selection, capped: false };
  if (from < 0) return toggle(selection, id);

  const [start, end] = from <= to ? [from, to] : [to, from];
  const ids = new Set(selection.ids);
  let capped = false;
  for (const each of order.slice(start, end + 1)) {
    if (ids.has(each)) continue;
    if (ids.size >= MAX_SELECTED) {
      capped = true;
      break;
    }
    ids.add(each);
  }
  return { selection: { ids, anchor: selection.anchor }, capped };
}

/** Selects the loaded messages of the list, at most the limit of them, from the top. */
export function selectAll(order: readonly string[]): SelectionChange {
  return {
    selection: { ids: new Set(order.slice(0, MAX_SELECTED)), anchor: null },
    capped: order.length > MAX_SELECTED,
  };
}

/** The selection without the messages that are no longer in the list (moved, deleted, or the list changed). */
export function keepListed(selection: Selection, order: readonly string[]): Selection {
  const listed = new Set(order);
  if ([...selection.ids].every((id) => listed.has(id))) return selection;
  return {
    ids: new Set([...selection.ids].filter((id) => listed.has(id))),
    anchor: selection.anchor !== null && listed.has(selection.anchor) ? selection.anchor : null,
  };
}

/** The selected ids in the order of the list. */
export function selectedInOrder(selection: Selection, order: readonly string[]): string[] {
  return order.filter((id) => selection.ids.has(id));
}
