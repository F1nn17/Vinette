import { describe, expect, it } from 'vitest';
import css from './tokens.css?inline';

// WCAG 1.4.3 (VIN-130): the text colors of tokens.css keep a contrast of 4.5 : 1 with the backgrounds they are used on. axe checks the
// screens it is shown; this checks every pair, also those of states a scenario does not reach (hints, errors, labels, the dark panel).
// The accent a server sets (Branding:Accent) is checked by the backend (AccentContrast).

const tokens = new Map(
  [...css.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-f]{6})\s*;/g)].map((m) => [m[1], m[2]]),
);

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const v = Number.parseInt(hex.slice(i, i + 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

function contrast(foreground: string, background: string): number {
  const f = tokens.get(foreground);
  const b = tokens.get(background);
  if (!f || !b) throw new Error(`No token --${f ? background : foreground} in tokens.css`);
  const [light, dark] = [luminance(f), luminance(b)].sort((x, y) => y - x);
  return (light! + 0.05) / (dark! + 0.05);
}

const pairs: [string, string][] = [
  ...['ink', 'text-body', 'text-2', 'text-3', 'muted', 'accent', 'flag'].flatMap(
    (text): [string, string][] => [
      [text, 'bg'],
      [text, 'surface'],
      [text, 'surface-2'],
    ],
  ),
  ['on-accent', 'accent'],
  ['on-accent', 'accent-hover'],
  ['label-work-text', 'label-work-bg'],
  ['label-bills-text', 'label-bills-bg'],
  ['label-personal-text', 'label-personal-bg'],
  ['success-text', 'success-bg'],
  ['warning-text', 'warning-bg'],
  ['panel-text', 'panel-bg'],
  ['panel-text-2', 'panel-bg'],
  ['panel-text-3', 'panel-bg'],
];

describe('the text colors of the design tokens', () => {
  it.each(pairs)('%s on %s has a contrast of at least 4.5 : 1', (foreground, background) => {
    expect(contrast(foreground, background)).toBeGreaterThanOrEqual(4.5);
  });
});

// WCAG 1.4.11: the edge that shows where a field or a button is keeps a contrast of 3 : 1 with what is around it.
const edges: [string, string][] = [
  ['border-input', 'surface'],
  ['border-input', 'bg'],
  ['border-control', 'surface'],
  ['border-control', 'bg'],
];

describe('the edges of controls in the design tokens', () => {
  it.each(edges)('%s on %s has a contrast of at least 3 : 1', (edge, background) => {
    expect(contrast(edge, background)).toBeGreaterThanOrEqual(3);
  });
});
