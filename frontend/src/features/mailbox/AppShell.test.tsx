import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppShell } from './AppShell';
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

function renderShell(messageOpen = false, onBack = vi.fn()) {
  render(
    <AppShell
      messageOpen={messageOpen}
      onBack={onBack}
      nav={<p>folders-content</p>}
      list={<p>list-content</p>}
      reader={<p>reader-content</p>}
    />,
  );
  return onBack;
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

  it('on mobile shows only the list until a message is open, then only the message with a back button', async () => {
    fakeWindow(400);
    const user = userEvent.setup();
    const { unmount } = render(
      <AppShell
        messageOpen={false}
        onBack={vi.fn()}
        nav="n"
        list={<p>list-content</p>}
        reader={<p>reader-content</p>}
      />,
    );
    expect(screen.getByText('list-content')).toBeInTheDocument();
    expect(screen.queryByText('reader-content')).not.toBeInTheDocument();
    unmount();

    const onBack = renderShell(true);
    expect(screen.queryByText('list-content')).not.toBeInTheDocument();
    expect(screen.getByText('reader-content')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Назад к списку' }));
    expect(onBack).toHaveBeenCalledOnce();
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
