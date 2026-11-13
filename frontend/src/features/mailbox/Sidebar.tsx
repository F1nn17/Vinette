import {
  AlertCircle,
  Archive,
  ChevronDown,
  FileText,
  Flag,
  Folder as FolderIcon,
  Inbox,
  Pencil,
  Send,
  Trash2,
  type LucideIcon,
} from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate } from 'react-router';
import { describeApiError } from '@/shared/api/describe-error';
import { cn, formatBytes } from '@/shared/lib';
import { Button, Logo, ProgressBar } from '@/shared/ui';
import { SidebarLabels } from '@/features/labels';
import { useFolders, useQuota, type Folder } from './api';
import { folderLabel, folderSlug } from './folder-label';
import { useShell } from './shell-context';

const ICONS: Record<string, LucideIcon> = {
  inbox: Inbox,
  drafts: FileText,
  sent: Send,
  archive: Archive,
  junk: AlertCircle,
  trash: Trash2,
};

/** Drafts show how many there are; the other folders show how many are unread. */
function counterOf(folder: Folder): number {
  return Number(folder.role === 'drafts' ? folder.total : folder.unread);
}

interface RowProps {
  to: string;
  active: boolean;
  icon: LucideIcon;
  label: string;
  count?: number;
  rail: boolean;
  onNavigate: () => void;
}

function NavRow({ to, active, icon: Icon, label, count, rail, onNavigate }: RowProps) {
  return (
    <Link
      to={to}
      onClick={onNavigate}
      aria-current={active ? 'page' : undefined}
      title={rail ? label : undefined}
      className={cn(
        'flex h-10 items-center gap-3 rounded-sm px-3 text-base transition-colors max-md:h-11',
        rail && 'justify-center px-0',
        active ? 'bg-accent-soft font-semibold text-accent' : 'text-text-2 hover:bg-surface',
      )}
    >
      <Icon size={18} strokeWidth={1.8} aria-hidden className="shrink-0" />
      <span className={rail ? 'sr-only' : 'min-w-0 flex-1 truncate'}>{label}</span>
      {count !== undefined && count > 0 && (
        <span
          className={cn(
            'text-secondary tabular-nums',
            rail ? 'sr-only' : active ? 'font-semibold' : 'text-muted',
          )}
        >
          {count}
        </span>
      )}
    </Link>
  );
}

export interface SidebarProps {
  /** The signed-in mailbox address, shown on the (for now static) account switcher. */
  address: string;
  /** The folder in the address (`/mail/<slug>`), see `folderSlug`. */
  folderSlug: string;
  /** The label whose list is open (`/mail/@label/<name>`), if any: then no folder is the current one. */
  labelName?: string;
}

/**
 * The folder panel (DESIGN.md §5): logo, account, "Compose", folders with counters, labels, storage. On a tablet it is a
 * rail of icons; on mobile the same content sits in a drawer.
 */
export function Sidebar({ address, folderSlug: current, labelName }: SidebarProps) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { search } = useLocation();
  const { layout, closeNav } = useShell();
  const folders = useFolders();
  const quota = useQuota();
  const rail = layout === 'tablet';
  const flaggedView = new URLSearchParams(search).get('filter') === 'flagged';

  const shown = (folders.data ?? []).filter((f) => f.canContainMessages && f.subscribed);
  const rows: { key: string; node: ReactNode }[] = shown.map((f) => ({
    key: f.path,
    node: (
      <NavRow
        to={`/mail/${encodeURIComponent(folderSlug(f))}`}
        active={folderSlug(f) === current && !flaggedView}
        icon={ICONS[f.role] ?? FolderIcon}
        label={folderLabel(f, t)}
        count={counterOf(f)}
        rail={rail}
        onNavigate={closeNav}
      />
    ),
  }));
  // "Flagged" is not a folder but a view of the inbox; it sits right after it, as in the design.
  const flagged = {
    key: '\u0000flagged',
    node: (
      <NavRow
        to="/mail/inbox?filter=flagged"
        active={current === 'inbox' && flaggedView}
        icon={Flag}
        label={t('folders.flagged')}
        rail={rail}
        onNavigate={closeNav}
      />
    ),
  };
  const inboxAt = shown.findIndex((f) => f.role === 'inbox');
  rows.splice(inboxAt + 1, 0, flagged);

  const storage = quota.data?.storage;

  return (
    <div
      className={cn('flex min-h-full flex-col gap-4 py-5', rail ? 'items-center px-2' : 'px-3.5')}
    >
      <div className={cn(!rail && 'px-1.5')}>
        <Logo size={32} showName={!rail} />
      </div>

      {!rail && (
        <div
          className="flex h-9 items-center justify-between rounded-md border border-control bg-surface px-3 text-secondary text-ink"
          title={t('sidebar.account')}
        >
          <span className="truncate">{address}</span>
          <ChevronDown size={16} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
        </div>
      )}

      {rail ? (
        <Button
          size="lg"
          className="w-11 px-0"
          aria-label={t('sidebar.compose')}
          title={t('sidebar.compose')}
          onClick={() => void navigate('/compose')}
        >
          <Pencil size={18} strokeWidth={1.8} aria-hidden />
        </Button>
      ) : (
        <Button
          size="lg"
          fullWidth
          icon={<Pencil size={16} strokeWidth={1.8} aria-hidden />}
          onClick={() => {
            closeNav();
            void navigate('/compose');
          }}
        >
          {t('sidebar.compose')}
        </Button>
      )}

      <div className={cn('flex flex-col gap-0.5', rail && 'w-full')}>
        {folders.isPending && (
          <div role="status" aria-label={t('common.loading')} className="flex flex-col gap-0.5">
            {Array.from({ length: 6 }, (_, i) => (
              <div key={i} className="h-10 animate-pulse rounded-sm bg-line/60" />
            ))}
          </div>
        )}
        {folders.isError && (
          <div role="alert" className="px-1.5 text-secondary text-text-2">
            <p>{describeApiError(folders.error, t)}</p>
            <Button variant="ghost" size="sm" onClick={() => void folders.refetch()}>
              {t('common.retry')}
            </Button>
          </div>
        )}
        {folders.isSuccess && rows.map((row) => <div key={row.key}>{row.node}</div>)}
      </div>

      {!rail && <SidebarLabels activeLabel={labelName} onNavigate={closeNav} />}

      {!rail && storage && (
        <div className="mt-auto flex flex-col gap-2 px-1.5">
          <p className="text-meta text-text-2">
            {t('sidebar.storage', {
              used: formatBytes(Number(storage.usedBytes), i18n.language),
              limit: formatBytes(Number(storage.limitBytes), i18n.language),
            })}
          </p>
          <ProgressBar
            aria-label={t('sidebar.storageBar')}
            value={Number(storage.usedBytes)}
            max={Number(storage.limitBytes)}
            tone="ink"
          />
        </div>
      )}
    </div>
  );
}
