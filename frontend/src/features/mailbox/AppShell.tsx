import { ArrowLeft, Menu } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/shared/lib';
import { IconButton } from '@/shared/ui';
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
  /** Called by the "back" button on mobile. */
  onBack: () => void;
}

/**
 * The three-column frame of the mailbox (DESIGN.md §5, §8): 248 | 408 | rest on a desktop, a 64 px rail beside list and
 * reader on a tablet, one column on mobile. Semantic regions: nav, section (the list), main (the reading area).
 */
export function AppShell({ nav, list, reader, messageOpen, onBack }: AppShellProps) {
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

  const state = useMemo<ShellState>(
    () => ({
      layout,
      navOpen: drawerOpen,
      openNav: () => setNavOpen(true),
      closeNav: () => setNavOpen(false),
    }),
    [layout, drawerOpen],
  );

  const showList = !drawer || !messageOpen;
  const showReader = !drawer || messageOpen;

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
        {showList && (
          <section
            aria-label={t('mailbox.list')}
            className="min-w-0 overflow-y-auto border-x border-line bg-surface"
          >
            {drawer && (
              <div className="p-2">
                <IconButton aria-label={t('mailbox.menu')} onClick={state.openNav}>
                  <Menu size={18} strokeWidth={1.8} aria-hidden />
                </IconButton>
              </div>
            )}
            {list}
          </section>
        )}
        {showReader && (
          <main aria-label={t('mailbox.reader')} className="min-w-0 overflow-y-auto bg-surface">
            {drawer && (
              <div className="p-2">
                <IconButton aria-label={t('mailbox.back')} onClick={onBack}>
                  <ArrowLeft size={18} strokeWidth={1.8} aria-hidden />
                </IconButton>
              </div>
            )}
            {reader}
          </main>
        )}
      </div>
    </ShellContext.Provider>
  );
}
