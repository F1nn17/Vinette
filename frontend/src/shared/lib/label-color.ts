import type { LabelColor } from '../ui/LabelTag';

const COLORS: LabelColor[] = ['work', 'bills', 'personal'];

/**
 * A color for a label. Until labels have settings of their own (colors the user picks), the same label always gets the
 * same one of the three colors of the design.
 */
export function labelColor(label: string): LabelColor {
  let hash = 0;
  for (const char of label) hash = (hash * 31 + (char.codePointAt(0) ?? 0)) >>> 0;
  return COLORS[hash % COLORS.length] ?? 'work';
}
