import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/shared/lib';
import { ShellContext, type ShellState } from './shell-context';
import { useLayout } from './use-layout';

export interface AppShellProps {
  /** The folder panel (sidebar). On a tablet it is a 64 px rail, on mobile a drawer. */
  nav: ReactNode;
  /** The message list. */
  list: ReactNode;
  /** The reading area. */
  reader: ReactNode;
  /** Whether a message is open: on mobile it then takes the whole screen. */
  messageOpen: boolean;
}

/**
 * The three-column frame of the mailbox (DESIGN.md §5, §8): 248 | 408 | rest on a desktop, a 64 px rail beside list and
 * reader on a tablet, one column on mobile. Semantic regions: nav, section (the list), main (the reading area).
 */
export function AppShell({ nav, list, reader, messageOpen }: AppShellProps) {
  const { t } = useTranslation();
  const layout = useLayout();
  const [navOpen, setNavOpen] = useState(false);

  const drawer = layout === 'mobile';
  const drawerOpen = drawer && navOpen;

  useEffect(() => {
    if (!drawerOpen) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setNavOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [drawerOpen]);

  const showList = !drawer || !messageOpen;
  const showReader = !drawer || messageOpen;

  const state = useMemo<ShellState>(
    () => ({
      layout,
      navOpen: drawerOpen,
      openNav: () => setNavOpen(true),
      closeNav: () => setNavOpen(false),
    }),
    [layout, drawerOpen],
  );

  // On a phone opening a message and going back are screen changes: the focus goes where the reader's eyes are, to the
  // message, and back to the row that was open. (Not on the first render, nor on a desktop where nothing moves.)
  const reading = drawer && messageOpen;
  const readerRef = useRef<HTMLElement>(null);
  const listRef = useRef<HTMLElement>(null);
  const wasReading = useRef(reading);
  const openedRow = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (reading) {
      // The row that is open now: the address no longer names it once the list is back, so it is remembered here.
      openedRow.current =
        listRef.current?.querySelector<HTMLElement>('[aria-current="true"]') ?? null;
    }

    if (wasReading.current === reading) return;
    wasReading.current = reading;
    if (reading) {
      readerRef.current?.focus({ preventScroll: true });
    } else if (openedRow.current?.isConnected) {
      openedRow.current.focus({ preventScroll: true });
    }
  }, [reading]);

  return (
    <ShellContext.Provider value={state}>
      <div
        data-layout={layout}
        className={cn(
          'grid h-dvh overflow-hidden bg-bg',
          layout === 'desktop' && 'grid-cols-[248px_408px_minmax(0,1fr)]',
          layout === 'tablet' && 'grid-cols-[64px_minmax(280px,408px)_minmax(0,1fr)]',
          layout === 'mobile' && 'grid-cols-1',
        )}
      >
        {drawerOpen && (
          <div
            data-testid="nav-backdrop"
            className="fixed inset-0 z-10 bg-ink/40"
            onClick={() => setNavOpen(false)}
          />
        )}
        <nav
          aria-label={t('mailbox.nav')}
          hidden={drawer && !drawerOpen}
          className={cn(
            'overflow-y-auto bg-bg',
            drawer && 'fixed inset-y-0 left-0 z-20 w-[280px] max-w-[85vw] shadow-card',
          )}
        >
          {nav}
        </nav>
        {/* On a phone the message covers the list (same grid cell) and the list is only made invisible: what was loaded and where it was scrolled to is still there when the reader goes back. */}
        <section
          ref={listRef}
          aria-label={t('mailbox.list')}
          // Out of sight but still laid out, not removed from the page: a box that is removed forgets how far it was scrolled.
          style={showList ? undefined : { visibility: 'hidden' }}
          className={cn(
            'flex min-h-0 min-w-0 flex-col overflow-hidden border-x border-line bg-surface',
            drawer && 'col-start-1 row-start-1',
          )}
        >
          {list}
        </section>
        {showReader && (
          <main
            ref={readerRef}
            tabIndex={-1}
            aria-label={t('mailbox.reader')}
            className={cn(
              'min-h-0 min-w-0 overflow-hidden bg-surface outline-none',
              drawer && 'col-start-1 row-start-1',
            )}
          >
            {reader}
          </main>
        )}
      </div>
    </ShellContext.Provider>
  );
}
