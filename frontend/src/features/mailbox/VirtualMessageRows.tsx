import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, type ReactNode } from 'react';

/** One entry of the flat list: a date heading or a message row. */
export type ListEntry<T> =
  { type: 'heading'; key: string; label: string } | { type: 'row'; key: string; item: T };

const HEADING_HEIGHT = 36;
const ROW_HEIGHT = 96;
/** How many rows before the end the next page is asked for, so that it is there before the user scrolls to it. */
const LOAD_AHEAD = 10;

export interface VirtualMessageRowsProps<T> {
  /** The scrolling container the rows live in. */
  scrollElement: HTMLElement | null;
  entries: ListEntry<T>[];
  renderRow: (item: T) => ReactNode;
  /** Whether there is a next page to load. */
  hasMore: boolean;
  loadingMore: boolean;
  onLoadMore: () => void;
  /** What stands under the last row: a spinner, or "try again" after a failed page. */
  footer?: ReactNode;
}

/**
 * Only the rows near the visible part are in the page, however long the list is (DESIGN.md §5 asks for 50 000 messages
 * without lag): rows are measured as they appear, the others are absent. The next page is asked for when the user comes
 * within a few rows of the end.
 */
export function VirtualMessageRows<T>({
  scrollElement,
  entries,
  renderRow,
  hasMore,
  loadingMore,
  onLoadMore,
  footer,
}: VirtualMessageRowsProps<T>) {
  // The virtualizer's functions cannot be memoized by the React compiler; nothing here depends on that.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: entries.length,
    getScrollElement: () => scrollElement,
    estimateSize: (index) => (entries[index]?.type === 'heading' ? HEADING_HEIGHT : ROW_HEIGHT),
    getItemKey: (index) => entries[index]?.key ?? index,
    overscan: 6,
  });

  const virtualItems = virtualizer.getVirtualItems();
  const lastVisible = virtualItems.at(-1)?.index ?? -1;

  useEffect(() => {
    if (hasMore && !loadingMore && lastVisible >= entries.length - LOAD_AHEAD) onLoadMore();
  }, [hasMore, loadingMore, lastVisible, entries.length, onLoadMore]);

  return (
    <div>
      <div className="relative w-full" style={{ height: virtualizer.getTotalSize() }}>
        {virtualItems.map((virtual) => {
          const entry = entries[virtual.index];
          if (!entry) return null;
          return (
            <div
              key={virtual.key}
              data-index={virtual.index}
              ref={virtualizer.measureElement}
              className="absolute top-0 left-0 w-full"
              style={{ transform: `translateY(${virtual.start}px)` }}
            >
              {entry.type === 'heading' ? (
                <h2 className="bg-surface px-5 pt-3.5 pb-1.5 text-caps font-semibold tracking-wider text-muted uppercase">
                  {entry.label}
                </h2>
              ) : (
                renderRow(entry.item)
              )}
            </div>
          );
        })}
      </div>
      {footer}
    </div>
  );
}
