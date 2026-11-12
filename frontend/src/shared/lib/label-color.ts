import type { LabelColor } from '../ui/LabelTag';

const COLORS: LabelColor[] = ['work', 'bills', 'personal'];

/**
 * The color of a label: the one the user chose (`chosen`, the label colors of the settings; the name is compared without
 * regard to case, a value outside the palette is ignored), otherwise one of the three colors of the design picked by the
 * name, so that the same label always looks the same.
 */
export function labelColor(label: string, chosen?: Readonly<Record<string, string>>): LabelColor {
  const wanted = label.toLowerCase();
  const key = chosen
    ? Object.keys(chosen).find((name) => name.toLowerCase() === wanted)
    : undefined;
  const picked = key === undefined ? undefined : COLORS.find((color) => color === chosen?.[key]);
  if (picked) return picked;

  let hash = 0;
  for (const char of label) hash = (hash * 31 + (char.codePointAt(0) ?? 0)) >>> 0;
  return COLORS[hash % COLORS.length] ?? 'work';
}
