import { Check, ChevronDown, LogOut, Settings } from 'lucide-react';
import { useEffect, useId, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Avatar } from '@/shared/ui';
import { cn } from '@/shared/lib';

export interface AccountMenuProps {
  /** The signed-in mailbox address. */
  address: string;
  /** Ends the session; without it the menu has no "Sign out" (nothing to end). */
  onSignOut?: () => void;
  /** The trigger: the wide button with the address (sidebar, drawer) or a round button with the initials (the tablet's rail). */
  variant?: 'wide' | 'rail';
  /** Called when something was chosen that leaves the page, so a drawer can close. */
  onNavigate?: () => void;
}

const ITEM =
  'flex h-10 w-full items-center gap-3 rounded-sm px-3 text-left text-base text-ink outline-none transition-colors hover:bg-bg focus-visible:bg-bg focus-visible:outline-2 focus-visible:outline-offset-[-2px] focus-visible:outline-accent max-md:h-11';

/**
 * The account menu (DESIGN.md §5, "Переключатель аккаунта", VIN-120): the button with the address opens a list of accounts (for now the one
 * that is signed in, marked as current; the list is where more accounts will stand), then "Settings" and "Sign out". The panel is placed
 * beside the button with fixed coordinates, so the scrolling sidebar and the narrow rail cannot cut it off. It follows the menu pattern:
 * Arrow keys, Home and End move between the items, Escape and a click outside close it and give the focus back to the button.
 */
export function AccountMenu({
  address,
  onSignOut,
  variant = 'wide',
  onNavigate,
}: AccountMenuProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [place, setPlace] = useState<CSSProperties>({});
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const panelId = useId();

  const close = (refocus: boolean) => {
    setOpen(false);
    if (refocus) trigger.current?.focus();
  };

  const toggle = () => {
    if (!open && trigger.current) {
      const rect = trigger.current.getBoundingClientRect();
      // Under the button in the sidebar; beside the rail, level with the button.
      setPlace(
        variant === 'rail'
          ? { top: rect.top, left: rect.right + 8, width: 260 }
          : { top: rect.bottom + 4, left: rect.left, width: Math.max(rect.width, 240) },
      );
    }
    setOpen((value) => !value);
  };

  const items = () =>
    Array.from(panel.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);

  // Opening puts the focus on the first item, so the keyboard works at once.
  useEffect(() => {
    if (open) items()[0]?.focus();
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onPointer = (event: PointerEvent) => {
      const target = event.target as Node;
      if (!panel.current?.contains(target) && !trigger.current?.contains(target)) setOpen(false);
    };
    const onResize = () => setOpen(false);
    document.addEventListener('pointerdown', onPointer);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      window.removeEventListener('resize', onResize);
    };
  }, [open]);

  const onKeyDown = (event: KeyboardEvent) => {
    const all = items();
    const at = all.indexOf(document.activeElement as HTMLElement);
    const go = (index: number) => {
      event.preventDefault();
      all[(index + all.length) % all.length]?.focus();
    };
    if (event.key === 'ArrowDown') go(at + 1);
    else if (event.key === 'ArrowUp') go(at - 1);
    else if (event.key === 'Home') go(0);
    else if (event.key === 'End') go(all.length - 1);
    else if (event.key === 'Escape') {
      event.preventDefault();
      close(true);
    } else if (event.key === 'Tab') close(false);
  };

  return (
    <>
      {variant === 'rail' ? (
        <button
          ref={trigger}
          type="button"
          aria-haspopup="menu"
          aria-expanded={open}
          aria-controls={open ? panelId : undefined}
          aria-label={t('account.menu', { address })}
          title={address}
          onClick={toggle}
          className="rounded-full outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
        >
          <span aria-hidden="true">
            <Avatar name={address} size={40} />
          </span>
        </button>
      ) : (
        <button
          ref={trigger}
          type="button"
          aria-haspopup="menu"
          aria-expanded={open}
          aria-controls={open ? panelId : undefined}
          aria-label={t('account.menu', { address })}
          title={address}
          onClick={toggle}
          className="flex h-9 w-full items-center justify-between gap-2 rounded-md border border-control bg-surface px-3 text-secondary text-ink transition-colors hover:bg-bg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
        >
          <span className="truncate">{address}</span>
          <ChevronDown
            size={16}
            strokeWidth={1.8}
            aria-hidden
            className={cn('shrink-0 text-muted transition-transform', open && 'rotate-180')}
          />
        </button>
      )}
      {open && (
        <div
          ref={panel}
          id={panelId}
          role="menu"
          aria-label={t('account.title')}
          onKeyDown={onKeyDown}
          style={place}
          className="fixed z-30 flex flex-col gap-0.5 rounded-md border border-line bg-surface p-1.5 shadow-card"
        >
          {/* The accounts: one for now, the current. More will stand in this list. */}
          <div
            role="menuitemradio"
            aria-checked="true"
            tabIndex={-1}
            className="flex h-10 items-center gap-3 rounded-sm px-3 text-base font-semibold text-ink outline-none max-md:h-11"
          >
            <Check size={18} strokeWidth={1.8} aria-hidden className="shrink-0 text-accent" />
            <span className="min-w-0 truncate">{address}</span>
          </div>
          <div role="separator" className="my-1 h-px bg-line" />
          <Link
            role="menuitem"
            to="/settings"
            className={ITEM}
            onClick={() => {
              setOpen(false);
              onNavigate?.();
            }}
          >
            <Settings size={18} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
            {t('account.settings')}
          </Link>
          {onSignOut && (
            <button
              role="menuitem"
              type="button"
              className={ITEM}
              onClick={() => {
                setOpen(false);
                onNavigate?.();
                onSignOut();
              }}
            >
              <LogOut size={18} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
              {t('account.signOut')}
            </button>
          )}
        </div>
      )}
    </>
  );
}
