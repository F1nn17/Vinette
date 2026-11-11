import { RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { describeApiError } from '@/shared/api/describe-error';
import { Button, Chip, IconButton, SearchField } from '@/shared/ui';
import { useFolders, useMessages, type ListFilter } from './api';
import { folderLabel, folderSlug } from './folder-label';
import { MessageRow } from './MessageRow';

const FILTERS: ListFilter[] = ['all', 'unread', 'flagged'];

function filterOf(value: string | null): ListFilter {
  return value === 'unread' || value === 'flagged' ? value : 'all';
}

export interface MessageListPaneProps {
  /** The folder in the address, see `folderSlug`. */
  folderSlug: string;
  /** The open message, if any; its row is highlighted. */
  messageId?: string;
}

/**
 * The list column (DESIGN.md §5): title and unread count, refresh, search (not wired yet), filter chips, rows. Pages are
 * loaded on request ("Show more"); the filter lives in the address (`?filter=`), so it survives a reload and is shared
 * with the sidebar's "Flagged".
 */
export function MessageListPane({ folderSlug: slug, messageId }: MessageListPaneProps) {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const filter = filterOf(params.get('filter'));
  const folders = useFolders();
  const folder = folders.data?.find((f) => folderSlug(f) === slug);
  const messages = useMessages(folder?.path, filter);

  const items = messages.data?.pages.flatMap((p) => p.items) ?? [];
  const unread = folder ? Number(folder.unread) : 0;
  const search = params.toString() ? `?${params.toString()}` : '';
  const now = new Date();
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
    <div className="flex min-h-full flex-col">
      <header className="flex flex-col gap-3.5 px-5 pt-[18px] pb-3">
        <div className="flex items-center gap-2">
          <h1 className="flex min-w-0 flex-1 items-baseline gap-2">
            <span className="truncate text-heading font-bold text-ink">{title}</span>
            {unread > 0 && (
              <span className="shrink-0 text-secondary text-muted">
                {t('list.unreadCount', { count: unread })}
              </span>
            )}
          </h1>
          <IconButton aria-label={t('list.refresh')} onClick={refresh}>
            <RefreshCw size={18} strokeWidth={1.8} aria-hidden />
          </IconButton>
        </div>
        <SearchField aria-label={t('list.search')} placeholder={t('list.search')} />
        <div role="group" aria-label={t('list.filters')} className="flex flex-wrap gap-2">
          {FILTERS.map((f) => (
            <Chip key={f} pressed={filter === f} onClick={() => setFilter(f)}>
              {t(`list.filter.${f}`)}
            </Chip>
          ))}
        </div>
      </header>

      <div className="flex-1 border-t border-subtle" aria-busy={loading}>
        {loading && (
          <div role="status" aria-label={t('common.loading')}>
            {Array.from({ length: 8 }, (_, i) => (
              <div key={i} className="border-b border-subtle px-5 py-3.5">
                <div className="h-3.5 w-1/3 animate-pulse rounded-xs bg-line" />
                <div className="mt-2 h-3.5 w-3/4 animate-pulse rounded-xs bg-line/70" />
                <div className="mt-2 h-3 w-full animate-pulse rounded-xs bg-line/50" />
              </div>
            ))}
          </div>
        )}

        {error && (
          <div role="alert" className="flex flex-col items-start gap-2 p-5 text-text-2">
            <p>{describeApiError(error, t)}</p>
            <Button variant="secondary" size="sm" onClick={refresh}>
              {t('common.retry')}
            </Button>
          </div>
        )}

        {missing && !error && <p className="p-5 text-muted">{t('list.noFolder')}</p>}

        {!loading && !error && folder && items.length === 0 && (
          <p className="p-5 text-muted">{t('list.empty')}</p>
        )}

        {items.length > 0 && (
          <ul aria-label={t('list.messages')}>
            {items.map((m) => (
              <li key={m.id}>
                <MessageRow
                  message={m}
                  to={`/mail/${encodeURIComponent(slug)}/${m.id}${search}`}
                  selected={m.id === messageId}
                  showRecipient={showRecipient}
                  now={now}
                />
              </li>
            ))}
          </ul>
        )}

        {messages.hasNextPage && (
          <div className="p-4">
            <Button
              variant="secondary"
              fullWidth
              disabled={messages.isFetchingNextPage}
              onClick={() => void messages.fetchNextPage()}
            >
              {messages.isFetchingNextPage ? t('common.loading') : t('list.more')}
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}
