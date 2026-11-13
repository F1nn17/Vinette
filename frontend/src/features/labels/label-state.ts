import type { Label } from './api';

/** Whether a label is on all of the messages, on some of them or on none. */
export type LabelState = 'all' | 'some' | 'none';

/** The colors the labels have (by name), the way `labelColor` wants them. */
export function colorsOf(labels: readonly Label[] | undefined): Record<string, string> {
  const colors: Record<string, string> = {};
  for (const label of labels ?? []) if (label.color) colors[label.name] = label.color;
  return colors;
}

/** What the box of a label shows: the state of the label among the messages the menu is for. */
export function stateOf(
  name: string,
  labelsOfMessages: readonly (readonly string[])[],
): LabelState {
  if (labelsOfMessages.length === 0) return 'none';
  const wanted = name.toLowerCase();
  const having = labelsOfMessages.filter((labels) =>
    labels.some((l) => l.toLowerCase() === wanted),
  ).length;
  return having === 0 ? 'none' : having === labelsOfMessages.length ? 'all' : 'some';
}
