import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useDocumentTitle } from './document-title';

function Screen({ title, priority }: { title: string | null; priority?: number }) {
  useDocumentTitle(title, priority);
  return null;
}

describe('useDocumentTitle', () => {
  it('names the tab after the screen and the product, and puts the product name back when the screen goes', () => {
    const { unmount } = render(<Screen title="Inbox" />);
    expect(document.title).toBe('Inbox — Vinette');

    unmount();
    expect(document.title).toBe('Vinette');
  });

  it('follows a change of the title', () => {
    const { rerender } = render(<Screen title="Inbox" />);
    rerender(<Screen title="Drafts" />);

    expect(document.title).toBe('Drafts — Vinette');
  });

  it('shows the screen with the higher priority while two are on, and the other again when it goes', () => {
    const folder = render(<Screen title="Inbox" />);
    const message = render(<Screen title="Weekly report" priority={1} />);
    expect(document.title).toBe('Weekly report — Vinette');

    message.unmount();
    expect(document.title).toBe('Inbox — Vinette');
    folder.unmount();
  });

  it('leaves the title alone when there is no title yet', () => {
    const { unmount } = render(<Screen title={null} />);
    expect(document.title).toBe('Vinette');
    unmount();
  });
});
