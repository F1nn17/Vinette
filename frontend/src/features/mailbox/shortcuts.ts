import { useEffect, useRef } from 'react';

/** What a hot key asks for (DESIGN.md section 5). */
export type ShortcutName =
  'next' | 'previous' | 'open' | 'archive' | 'delete' | 'search' | 'compose' | 'help';

/** Inputs where the user types text; a box, a radio button or a button is an input too, but nothing is typed into it. */
const TEXT_FIELD =
  'input:not([type="checkbox"], [type="radio"], [type="button"], [type="submit"], [type="reset"], [type="range"], [type="file"], [type="color"], [type="image"]), textarea, select, [contenteditable]:not([contenteditable="false"])';

/** Where typing happens: keys there are the user's own text, never shortcuts. */
function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || target.closest(TEXT_FIELD) !== null;
}

/** Something that already does its own thing with Enter or Space (a link, a button, a box): Enter is left to it. */
function isInteractive(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLElement &&
    target.closest(
      'a, button, summary, [role="button"], [role="link"], [role="checkbox"], [role="menuitem"]',
    ) !== null
  );
}

/**
 * The shortcut a key press stands for, or null. Keys are read by their place on the keyboard (`code`), so that they work in
 * the Russian layout too; `#` and `?` also by the character they print. Nothing happens with Ctrl, Alt or the system key
 * held (those belong to the browser), while a window is open over the screen, while typing, or while an input method is
 * composing text. Enter opens a message only when the focus is not on something that uses Enter itself.
 */
export function shortcutOf(
  event: Pick<
    KeyboardEvent,
    | 'key'
    | 'code'
    | 'shiftKey'
    | 'ctrlKey'
    | 'altKey'
    | 'metaKey'
    | 'isComposing'
    | 'defaultPrevented'
    | 'target'
  >,
): ShortcutName | null {
  if (event.defaultPrevented || event.isComposing) return null;
  if (event.ctrlKey || event.altKey || event.metaKey) return null;
  if (isTyping(event.target)) return null;
  if (document.querySelector('[role="dialog"]')) return null;

  if (event.key === '?' || (event.code === 'Slash' && event.shiftKey)) return 'help';
  if (event.key === '#' || (event.code === 'Digit3' && event.shiftKey)) return 'delete';
  if (event.shiftKey) return null;
  if (event.key === '/' || event.code === 'Slash') return 'search';
  if (event.key === 'Enter') return isInteractive(event.target) ? null : 'open';

  switch (event.code) {
    case 'KeyJ':
      return 'next';
    case 'KeyK':
      return 'previous';
    case 'KeyE':
      return 'archive';
    case 'KeyC':
      return 'compose';
    default:
      return null;
  }
}

export type ShortcutHandlers = Partial<Record<ShortcutName, () => void>>;

/**
 * Listens for the hot keys on the whole page while the component is shown. A key that has no handler is left alone (the
 * browser keeps it); one that has is taken (no scrolling on `/`, no typing it somewhere).
 */
export function useShortcuts(handlers: ShortcutHandlers) {
  const latest = useRef(handlers);
  useEffect(() => {
    latest.current = handlers;
  });
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const name = shortcutOf(event);
      const handler = name ? latest.current[name] : undefined;
      if (!handler) return;
      event.preventDefault();
      handler();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);
}
