import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppShell } from './AppShell';
import { NavMenuButton } from './NavMenuButton';
import { DESKTOP_MIN_WIDTH, TABLET_MIN_WIDTH } from './use-layout';

type Listener = () => void;

/** A window of the given width: matchMedia answers min-width queries and can notify about a resize. */
function fakeWindow(initialWidth: number) {
  let width = initialWidth;
  const listeners = new Set<Listener>();
  vi.stubGlobal('matchMedia', (query: string) => {
    const min = Number(/min-width: (\d+)px/.exec(query)?.[1]);
    return {
      get matches() {
        return width >= min;
      },
      media: query,
      addEventListener: (_: string, l: Listener) => listeners.add(l),
      removeEventListener: (_: string, l: Listener) => listeners.delete(l),
    };
  });
  return {
    resize(next: number) {
      width = next;
      act(() => listeners.forEach((l) => l()));
    },
  };
}

function renderShell(messageOpen = false) {
  return render(
    <AppShell
      messageOpen={messageOpen}
      nav={<p>folders-content</p>}
      list={
        <>
          <NavMenuButton />
          <p>list-content</p>
        </>
      }
      reader={<p>reader-content</p>}
    />,
  );
}

const layoutOf = () => document.querySelector('[data-layout]')?.getAttribute('data-layout');

afterEach(() => vi.unstubAllGlobals());

describe('AppShell', () => {
  it('shows three semantic regions on a desktop', () => {
    fakeWindow(DESKTOP_MIN_WIDTH);
    renderShell();

    expect(layoutOf()).toBe('desktop');
    expect(screen.getByRole('navigation', { name: 'Папки' })).toHaveTextContent('folders-content');
    expect(screen.getByRole('region', { name: 'Список писем' })).toHaveTextContent('list-content');
    expect(screen.getByRole('main')).toHaveTextContent('reader-content');
    expect(screen.queryByRole('button', { name: 'Меню' })).not.toBeInTheDocument();
  });

  it.each([
    [TABLET_MIN_WIDTH - 1, 'mobile'],
    [TABLET_MIN_WIDTH, 'tablet'],
    [DESKTOP_MIN_WIDTH - 1, 'tablet'],
    [DESKTOP_MIN_WIDTH, 'desktop'],
  ])('at %i px the layout is %s', (width, expected) => {
    fakeWindow(width);
    renderShell();

    expect(layoutOf()).toBe(expected);
  });

  it('follows the window when it is resized across the borders', () => {
    const win = fakeWindow(1400);
    renderShell();

    win.resize(1000);
    expect(layoutOf()).toBe('tablet');
    win.resize(500);
    expect(layoutOf()).toBe('mobile');
    win.resize(1200);
    expect(layoutOf()).toBe('desktop');
  });

  it('keeps list and reader side by side on a tablet', () => {
    fakeWindow(900);
    renderShell(true);

    expect(screen.getByText('list-content')).toBeInTheDocument();
    expect(screen.getByText('reader-content')).toBeInTheDocument();
  });

  it('on mobile shows only the list until a message is open, then only the message', () => {
    fakeWindow(400);
    const { unmount } = render(
      <AppShell
        messageOpen={false}
        nav="n"
        list={<p>list-content</p>}
        reader={<p>reader-content</p>}
      />,
    );
    expect(screen.getByText('list-content')).toBeVisible();
    expect(screen.queryByText('reader-content')).not.toBeInTheDocument();
    unmount();

    renderShell(true);
    expect(screen.getByText('reader-content')).toBeVisible();
    expect(screen.getByText('list-content')).not.toBeVisible(); // out of sight
  });

  it('on mobile the list stays in the page while a message is open, so that where it was is still there when the reader goes back', () => {
    fakeWindow(400);
    const list = <p data-testid="list-state">list</p>;
    const shell = (open: boolean) => (
      <AppShell messageOpen={open} nav="n" list={list} reader={<p>reader-content</p>} />
    );
    const { rerender } = render(shell(false));
    const before = screen.getByTestId('list-state');

    rerender(shell(true));
    expect(screen.getByTestId('list-state')).toBe(before); // the same element: not thrown away and built again
    expect(before).not.toBeVisible();

    rerender(shell(false));
    expect(screen.getByTestId('list-state')).toBe(before);
    expect(before).toBeVisible();
    expect(screen.queryByText('reader-content')).not.toBeInTheDocument();
  });

  it('on a desktop and a tablet the list is always on screen', () => {
    for (const width of [900, 1400]) {
      fakeWindow(width);
      const { unmount } = render(
        <AppShell messageOpen nav="n" list={<p>list-content</p>} reader={<p>reader-content</p>} />,
      );
      expect(screen.getByText('list-content')).toBeVisible();
      expect(screen.getByText('reader-content')).toBeVisible();
      unmount();
    }
  });

  it('on mobile moves the focus to the message when it opens, and back to the row that was open when it closes', () => {
    fakeWindow(400);
    const list = (
      <a href="#one" aria-current="true">
        row
      </a>
    );
    const shell = (open: boolean) => (
      <AppShell messageOpen={open} nav="n" list={list} reader={<p>reader-content</p>} />
    );
    const { rerender } = render(shell(false));
    expect(document.body).toHaveFocus(); // nothing moves on the first render

    rerender(shell(true));
    expect(screen.getByRole('main')).toHaveFocus();

    rerender(shell(false));
    expect(screen.getByRole('link', { name: 'row' })).toHaveFocus();
  });

  it('on a desktop opening a message does not take the focus away from the list', () => {
    fakeWindow(1400);
    const shell = (open: boolean) => (
      <AppShell
        messageOpen={open}
        nav="n"
        list={
          <a href="#one" aria-current="true">
            row
          </a>
        }
        reader={<p>reader-content</p>}
      />
    );
    const { rerender } = render(shell(false));
    screen.getByRole('link', { name: 'row' }).focus();

    rerender(shell(true));

    expect(screen.getByRole('link', { name: 'row' })).toHaveFocus();
  });

  it('on mobile the folders are a drawer: closed by default, opened by the menu, closed by Escape and by the backdrop', async () => {
    fakeWindow(400);
    const user = userEvent.setup();
    renderShell();

    expect(screen.queryByRole('navigation')).not.toBeInTheDocument(); // hidden
    await user.click(screen.getByRole('button', { name: 'Меню' }));
    expect(screen.getByRole('navigation', { name: 'Папки' })).toBeVisible();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Меню' }));
    await user.click(screen.getByTestId('nav-backdrop'));
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument();
  });
});
