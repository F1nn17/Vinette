import { useSyncExternalStore } from 'react';

/** Layout breakpoints from DESIGN.md §5 and §8: below 768 px mobile, 768–1199 px tablet, from 1200 px desktop. */
export const TABLET_MIN_WIDTH = 768;
export const DESKTOP_MIN_WIDTH = 1200;

export type Layout = 'mobile' | 'tablet' | 'desktop';

const tabletQuery = `(min-width: ${TABLET_MIN_WIDTH}px)`;
const desktopQuery = `(min-width: ${DESKTOP_MIN_WIDTH}px)`;

function subscribe(onChange: () => void) {
  if (typeof window.matchMedia !== 'function') return () => undefined;
  const lists = [window.matchMedia(tabletQuery), window.matchMedia(desktopQuery)];
  lists.forEach((list) => list.addEventListener('change', onChange));
  return () => lists.forEach((list) => list.removeEventListener('change', onChange));
}

function getSnapshot(): Layout {
  if (typeof window.matchMedia !== 'function') return 'desktop'; // no media queries: assume the widest layout
  if (window.matchMedia(desktopQuery).matches) return 'desktop';
  return window.matchMedia(tabletQuery).matches ? 'tablet' : 'mobile';
}

/** The current layout; follows the window as it is resized. */
export function useLayout(): Layout {
  return useSyncExternalStore(subscribe, getSnapshot, () => 'desktop');
}
