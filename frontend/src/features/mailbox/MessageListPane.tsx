import { RefreshCw, Search } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { dateGroupKey, dateGroupLabel, groupByDate } from '@/shared/lib';
import { cn } from '@/shared/lib';
import { Avatar, Chip, IconButton, SearchField } from '@/shared/ui';
import { useListDensity, useSettings, useUpdateSettings } from '@/features/settings';
import { useFolders, useMessages, type ListFilter, type MessageItem } from './api';
import { folderLabel, folderSlug } from './folder-label';
import { ListEmpty, ListError, ListNoFolder, ListSkeleton, OfflineBanner } from './list-states';
import { ListOptionsMenu } from './ListOptionsMenu';
import { MessageRow } from './MessageRow';
import { NavMenuButton } from './NavMenuButton';
import { useShell } from './shell-context';
import { ComposeFab } from './ComposeFab';
import { useOnline } from './use-online';
import { VirtualMessageRows, type ListEntry } from './VirtualMessageRows';

const FILTERS: ListFilter[] = ['all', 'unread', 'flagged'];

function filterOf(value: string | null): ListFilter {
  return value === 'unread' || value === 'flagged' ? value : 'all';
}

export interface MessageListPaneProps {
  /** The folder in the address, see `folderSlug`. */
  folderSlug: string;
  /** The open message, if any; its row is highlighted. */
  messageId?: string;
  /** The signed-in address: its initials are the account button of the mobile header. */
  address?: string;
}

/**
 * The list column (DESIGN.md §5): title and unread count, refresh, search (not wired yet), filter chips, rows. Pages are
 * loaded on request ("Show more"); the filter lives in the address (`?filter=`), so it survives a reload and is shared
 * with the sidebar's "Flagged".
 */
export function MessageListPane({ folderSlug: slug, messageId, address }: MessageListPaneProps) {
  const { t, i18n } = useTranslation();
  const [params, setParams] = useSearchParams();
  const filter = filterOf(params.get('filter'));
  const online = useOnline();
  const { layout } = useShell();
  const mobile = layout === 'mobile';
  const [searchOpen, setSearchOpen] = useState(false);
  const [scroller, setScroller] = useState<HTMLElement | null>(null);

  const folders = useFolders();
  const folder = folders.data?.find((f) => folderSlug(f) === slug);
  const settings = useSettings();
  const density = useListDensity();
  const updateSettings = useUpdateSettings();
  // Phones keep the preview whatever the choice (DESIGN.md §8 has one density).
  const compact = density === 'compact' && !mobile;
  const messages = useMessages(folder?.path, filter, !compact, !settings.isPending);

  const items = messages.data?.pages.flatMap((p) => p.items) ?? [];
  const unread = folder ? Number(folder.unread) : 0;
  const search = params.toString() ? `?${params.toString()}` : '';
  const now = new Date();
  const groupWords = {
    today: t('list.group.today'),
    yesterday: t('list.group.yesterday'),
    week: t('list.group.week'),
  };
  const entries: ListEntry<MessageItem>[] = groupByDate(
    items,
    (m) => new Date(m.date),
    now,
  ).flatMap(({ group, items: rows }) => [
    {
      type: 'heading' as const,
      key: `h:${dateGroupKey(group)}:${rows[0]?.id}`,
      label: dateGroupLabel(group, now, i18n.language, groupWords),
    },
    ...rows.map((item) => ({ type: 'row' as const, key: item.id, item })),
  ]);
  const showRecipient = folder?.role === 'sent' || folder?.role === 'drafts';
  const title = folder ? folderLabel(folder, t) : slug === 'inbox' ? t('folders.inbox') : '';
  const error = folders.isError ? folders.error : messages.isError ? messages.error : null;
  const missing = folders.isSuccess && !folder;
  const loading = folders.isPending || (folder !== undefined && messages.isPending);

  const refresh = () => {
    void folders.refetch();
    if (folder) void messages.refetch();
  };

  const setFilter = (next: ListFilter) => {
    const copy = new URLSearchParams(params);
    if (next === 'all') copy.delete('filter');
    else copy.set('filter', next);
    setParams(copy, { replace: true });
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header
        className={cn('flex flex-col px-5 pb-3', mobile ? 'gap-3 px-4 pt-3' : 'gap-3.5 pt-[18px]')}
      >
        <div className="flex items-center gap-2">
          {mobile && <NavMenuButton />}
          <h1 className="flex min-w-0 flex-1 items-baseline gap-2">
            <span
              className={cn('truncate font-bold text-ink', mobile ? 'text-lead' : 'text-heading')}
              style={mobile ? { fontSize: 20 } : undefined}
            >
              {title}
            </span>
            {unread > 0 && !mobile && (
              <span className="shrink-0 text-secondary text-muted">
                {t('list.unreadCount', { count: unread })}
              </span>
            )}
          </h1>
          {mobile ? (
            <>
              <IconButton
                aria-label={t('list.search')}
                aria-expanded={searchOpen}
                onClick={() => setSearchOpen((open) => !open)}
              >
                <Search size={20} strokeWidth={1.8} aria-hidden />
              </IconButton>
              {address && (
                <span aria-hidden="true">
                  <Avatar name={address} size={36} />
                </span>
              )}
            </>
          ) : (
            <>
              <IconButton aria-label={t('list.refresh')} onClick={refresh}>
                <RefreshCw size={18} strokeWidth={1.8} aria-hidden />
              </IconButton>
              <ListOptionsMenu
                density={density}
                onDensityChange={(next) => updateSettings.mutate({ density: next })}
              />
            </>
          )}
        </div>
        {(!mobile || searchOpen) && (
          <SearchField aria-label={t('list.search')} placeholder={t('list.search')} />
        )}
        <div role="group" aria-label={t('list.filters')} className="flex flex-wrap gap-2">
          {FILTERS.map((f) => (
            <Chip key={f} pressed={filter === f} onClick={() => setFilter(f)}>
              {t(`list.filter.${f}`)}
            </Chip>
          ))}
        </div>
      </header>

      {!online && <OfflineBanner />}

      <div
        ref={setScroller}
        className="min-h-0 flex-1 overflow-y-auto border-t border-subtle"
        aria-busy={loading}
      >
        {loading && <ListSkeleton />}

        {error && <ListError error={error} onRetry={refresh} />}

        {missing && !error && <ListNoFolder />}

        {!loading && !error && folder && items.length === 0 && <ListEmpty />}

        {items.length > 0 && (
          <VirtualMessageRows
            scrollElement={scroller}
            entries={entries}
            compact={compact}
            hasMore={messages.hasNextPage && !messages.isFetchNextPageError}
            loadingMore={messages.isFetchingNextPage}
            onLoadMore={() => void messages.fetchNextPage()}
            renderRow={(m) => (
              <MessageRow
                message={m}
                to={`/mail/${encodeURIComponent(slug)}/${m.id}${search}`}
                selected={m.id === messageId}
                showRecipient={showRecipient}
                now={now}
                avatar={mobile}
                compact={compact}
                labelColors={settings.data?.labelColors}
              />
            )}
            footer={
              messages.isFetchingNextPage ? (
                <div role="status" aria-label={t('common.loading')} className="p-4">
                  <div className="mx-auto h-3.5 w-1/3 animate-pulse rounded-xs bg-line" />
                </div>
              ) : messages.isFetchNextPageError ? (
                <ListError error={messages.error} onRetry={() => void messages.fetchNextPage()} />
              ) : null
            }
          />
        )}
      </div>
      {mobile && <ComposeFab />}
    </div>
  );
}
