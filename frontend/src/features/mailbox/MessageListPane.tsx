import { RefreshCw, Search } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { dateGroupKey, dateGroupLabel, groupByDate } from '@/shared/lib';
import { cn } from '@/shared/lib';
import { Avatar, Chip, IconButton, SearchField } from '@/shared/ui';
import { MessageLabelsMenu, labelPath, useLabelMessages } from '@/features/labels';
import { useListDensity, useSettings, useUpdateSettings } from '@/features/settings';
import { useFolders, useMessages, type ListFilter, type MessageItem } from './api';
import { folderOfId, useMessageBatch, type BatchAction } from './batch';
import { folderLabel, folderSlug } from './folder-label';
import { ListEmpty, ListError, ListNoFolder, ListSkeleton, OfflineBanner } from './list-states';
import { ListOptionsMenu } from './ListOptionsMenu';
import { MessageRow } from './MessageRow';
import { NavMenuButton } from './NavMenuButton';
import { ShortcutsDialog } from './ShortcutsDialog';
import { useShortcuts } from './shortcuts';
import { SelectionBar } from './SelectionBar';
import {
  EMPTY_SELECTION,
  extend,
  MAX_SELECTED,
  keepListed,
  selectAll,
  selectedInOrder,
  toggle,
  type Selection,
  type SelectionChange,
} from './selection';
import { useShell } from './shell-context';
import { ComposeFab } from './ComposeFab';
import { useOnline } from './use-online';
import { VirtualMessageRows, type ListEntry } from './VirtualMessageRows';

const FILTERS: ListFilter[] = ['all', 'unread', 'flagged'];

/** The actions that take a message out of the list. */
const MOVING: BatchAction[] = ['archive', 'spam', 'trash'];

function filterOf(value: string | null): ListFilter {
  return value === 'unread' || value === 'flagged' ? value : 'all';
}

export interface MessageListPaneProps {
  /** The folder in the address, see `folderSlug`. */
  folderSlug: string;
  /** The list is not of a folder but of the messages that have this label, from all folders (VIN-078). */
  labelName?: string;
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
export function MessageListPane({
  folderSlug: slug,
  labelName,
  messageId,
  address,
}: MessageListPaneProps) {
  const { t, i18n } = useTranslation();
  const [params, setParams] = useSearchParams();
  const labelMode = labelName !== undefined;
  // The list of a label has no filters: it is one page of what has the label, from all folders.
  const filter = labelMode ? 'all' : filterOf(params.get('filter'));
  const basePath = labelMode ? labelPath(labelName) : `/mail/${encodeURIComponent(slug)}`;
  const online = useOnline();
  const { layout } = useShell();
  const mobile = layout === 'mobile';
  const [searchOpen, setSearchOpen] = useState(false);
  const [scroller, setScroller] = useState<HTMLElement | null>(null);
  const navigate = useNavigate();
  const batch = useMessageBatch();
  // The selection belongs to one folder and filter: another list starts with nothing selected.
  const listKey = labelMode ? `@label|${labelName}` : `${slug}|${params.get('filter') ?? ''}`;
  const [selectionState, setSelectionState] = useState<{
    key: string;
    selection: Selection;
    capped: boolean;
  }>({ key: listKey, selection: EMPTY_SELECTION, capped: false });

  const folders = useFolders();
  const folder = folders.data?.find((f) => folderSlug(f) === slug);
  const settings = useSettings();
  const density = useListDensity();
  const updateSettings = useUpdateSettings();
  // Phones keep the preview whatever the choice (DESIGN.md §8 has one density).
  const compact = density === 'compact' && !mobile;
  const folderMessages = useMessages(
    labelMode ? undefined : folder?.path,
    filter,
    !compact,
    !settings.isPending,
  );
  const labelMessages = useLabelMessages(labelName, !compact, !settings.isPending);
  const messages = labelMode ? labelMessages : folderMessages;

  const items = messages.data?.pages.flatMap((p) => p.items) ?? [];
  const unread = folder && !labelMode ? Number(folder.unread) : 0;
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
  const order = items.map((m) => m.id);
  const current = selectionState.key === listKey ? selectionState : null;
  const selection = keepListed(current?.selection ?? EMPTY_SELECTION, order);
  const capped = current?.capped ?? false;
  const selectedIds = selectedInOrder(selection, order);
  const selecting = selectedIds.length > 0;
  const apply = (change: SelectionChange) =>
    setSelectionState({ key: listKey, selection: change.selection, capped: change.capped });
  const clearSelection = () => {
    batch.reset();
    setSelectionState({ key: listKey, selection: EMPTY_SELECTION, capped: false });
  };
  const runBatch = (action: BatchAction, ids: string[] = selectedIds) => {
    const before = selection;
    // The list shows the result at once (see `useMessageBatch`), so the selection ends now; if the server refuses, the
    // rows come back and so does the selection, and a toast says what happened.
    clearSelection();
    if (messageId && ids.includes(messageId) && MOVING.includes(action)) {
      // The open message goes to another folder: the reading pane would show a message that is no longer here.
      void navigate(`${basePath}${search}`);
    }
    batch.mutate({ ids, action }, { onError: () => apply({ selection: before, capped: false }) });
  };

  // The keyboard (VIN-077): a place in the list ("current") that j and k move and Enter opens. Without one the open message is it.
  const [cursor, setCursor] = useState<{ key: string; id: string } | null>(null);
  const [helpOpen, setHelpOpen] = useState(false);
  const [labelsOpen, setLabelsOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const currentId =
    cursor?.key === listKey && order.includes(cursor.id) ? cursor.id : (messageId ?? null);
  const openMessage = (id: string) => void navigate(`${basePath}/${id}${search}`);
  const moveCursor = (step: 1 | -1) => {
    if (order.length === 0) return;
    const at = currentId === null ? -1 : order.indexOf(currentId);
    const target = order[at === -1 ? 0 : Math.min(order.length - 1, Math.max(0, at + step))];
    if (target === undefined) return;
    setCursor({ key: listKey, id: target });
    // Reading: with a message open, the next one opens as the cursor reaches it.
    if (messageId !== undefined && target !== messageId) openMessage(target);
  };
  /** What an action from the keyboard applies to: the selected messages, else the open one, else the one under the cursor. */
  const targets = () => (selecting ? selectedIds : currentId === null ? [] : [currentId]);
  const roleOfId = (id: string) =>
    labelMode ? folders.data?.find((f) => f.path === folderOfId(id))?.role : folder?.role;
  const keyAction = (action: BatchAction, skipIn: string) => {
    const ids = targets().filter((id) => roleOfId(id) !== skipIn);
    if (ids.length > 0) runBatch(action, ids);
  };
  useShortcuts({
    next: () => moveCursor(1),
    previous: () => moveCursor(-1),
    open: () => {
      if (currentId !== null && currentId !== messageId) openMessage(currentId);
    },
    // The same actions as the buttons; they leave a message where it is already (no archive in the archive, no delete in the trash).
    archive: () => keyAction('archive', 'archive'),
    delete: () => keyAction('trash', 'trash'),
    search: () => {
      setSearchOpen(true);
      requestAnimationFrame(() => searchRef.current?.focus());
    },
    compose: () => void navigate('/compose'),
    help: () => setHelpOpen(true),
  });
  const showRecipient = !labelMode && (folder?.role === 'sent' || folder?.role === 'drafts');
  const title = labelMode
    ? labelName
    : folder
      ? folderLabel(folder, t)
      : slug === 'inbox'
        ? t('folders.inbox')
        : '';
  const error =
    folders.isError && !labelMode ? folders.error : messages.isError ? messages.error : null;
  const missing = folders.isSuccess && !folder && !labelMode;
  const loading = labelMode
    ? messages.isPending
    : folders.isPending || (folder !== undefined && messages.isPending);

  const refresh = () => {
    void folders.refetch();
    if (folder || labelMode) void messages.refetch();
  };

  const setFilter = (next: ListFilter) => {
    const copy = new URLSearchParams(params);
    if (next === 'all') copy.delete('filter');
    else copy.set('filter', next);
    setParams(copy, { replace: true });
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <ShortcutsDialog open={helpOpen} onClose={() => setHelpOpen(false)} />
      <header
        className={cn('flex flex-col px-5 pb-3', mobile ? 'gap-3 px-4 pt-3' : 'gap-3.5 pt-[18px]')}
      >
        {selecting ? (
          <>
            {/* the heading stays for screen readers and the page outline */}
            <h1 className="sr-only">{title}</h1>
            <SelectionBar
              count={selectedIds.length}
              allSelected={selectedIds.length === Math.min(order.length, MAX_SELECTED)}
              folderRole={folder?.role}
              capped={capped}
              onSelectAll={() => apply(selectAll(order))}
              onClear={clearSelection}
              onAction={runBatch}
              narrow={mobile}
              labels={{
                open: labelsOpen,
                onToggle: () => setLabelsOpen((open) => !open),
                menu: (
                  <MessageLabelsMenu
                    ids={selectedIds}
                    labelsOfMessages={selectedIds.map(
                      (id) => items.find((m) => m.id === id)?.labels ?? [],
                    )}
                    onClose={() => setLabelsOpen(false)}
                  />
                ),
              }}
            />
          </>
        ) : (
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
        )}
        {(!mobile || searchOpen) && (
          <SearchField
            inputRef={searchRef}
            aria-label={t('list.search')}
            placeholder={t('list.search')}
          />
        )}
        {!labelMode && (
          <div role="group" aria-label={t('list.filters')} className="flex flex-wrap gap-2">
            {FILTERS.map((f) => (
              <Chip key={f} pressed={filter === f} onClick={() => setFilter(f)}>
                {t(`list.filter.${f}`)}
              </Chip>
            ))}
          </div>
        )}
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

        {!loading && !error && (folder || labelMode) && items.length === 0 && (
          <ListEmpty label={labelMode} />
        )}

        {items.length > 0 && (
          <VirtualMessageRows
            scrollElement={scroller}
            entries={entries}
            compact={compact}
            scrollToKey={cursor?.key === listKey ? cursor.id : null}
            hasMore={messages.hasNextPage && !messages.isFetchNextPageError}
            loadingMore={messages.isFetchingNextPage}
            onLoadMore={() => void messages.fetchNextPage()}
            renderRow={(m) => (
              <MessageRow
                message={m}
                to={`${basePath}/${m.id}${search}`}
                selected={m.id === messageId}
                showRecipient={showRecipient}
                now={now}
                avatar={mobile}
                compact={compact}
                labelColors={settings.data?.labelColors}
                checked={selection.ids.has(m.id)}
                current={m.id === currentId && cursor?.key === listKey}
                selecting={selecting}
                // The messages of a label are in several folders, and an action is for one folder: no selection there.
                onSelect={
                  labelMode
                    ? undefined
                    : (how) =>
                        apply(
                          how === 'range'
                            ? extend(selection, order, m.id)
                            : toggle(selection, m.id),
                        )
                }
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
