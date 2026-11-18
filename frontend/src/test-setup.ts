import '@testing-library/jest-dom/vitest';
import '@/shared/i18n';

// ProseMirror (the message editor) measures the page while it moves the cursor into view; jsdom has no layout, so it measures nothing.
const rect = {
  x: 0,
  y: 0,
  width: 0,
  height: 0,
  top: 0,
  right: 0,
  bottom: 0,
  left: 0,
  toJSON: () => ({}),
};
const noRects = () =>
  ({
    length: 0,
    item: () => null,
    [Symbol.iterator]: [][Symbol.iterator],
  }) as unknown as DOMRectList;
if (typeof Range !== 'undefined') {
  Range.prototype.getBoundingClientRect = () => rect as DOMRect;
  Range.prototype.getClientRects = noRects;
}
if (typeof Element !== 'undefined') Element.prototype.getClientRects ??= noRects;
if (typeof document !== 'undefined') document.elementFromPoint ??= () => null;
