import { useEffect, useId } from 'react';
import { getBranding } from './branding';

// The title of the browser tab names the screen (WCAG 2.4.2): "Inbox — Vinette", "Settings — Vinette". A single-page application does not
// load a page when the screen changes, so without this the title stays the same and a screen reader or a list of tabs says nothing new.

interface Entry {
  title: string;
  priority: number;
}

const entries = new Map<string, Entry>();

function apply(): void {
  let best: Entry | undefined;
  for (const entry of entries.values()) {
    if (!best || entry.priority >= best.priority) best = entry;
  }
  const brand = getBranding().name;
  document.title = best ? `${best.title} — ${brand}` : brand;
}

/**
 * Names the screen in the title of the tab while the component is shown. When several are shown at once (the list and an open message), the
 * one with the higher priority wins (the message, 1, over its folder, 0); when none is shown the title is the name of the product.
 */
export function useDocumentTitle(title: string | null | undefined, priority = 0): void {
  const id = useId();
  useEffect(() => {
    if (!title) return undefined;
    entries.set(id, { title, priority });
    apply();
    return () => {
      entries.delete(id);
      apply();
    };
  }, [id, title, priority]);
}
