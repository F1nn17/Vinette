import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider, useLocation } from 'react-router';
import { AccountMenu, type AccountMenuProps } from './AccountMenu';

function Where() {
  return <output data-testid="where">{useLocation().pathname}</output>;
}

function show(props: Partial<AccountMenuProps> = {}) {
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: (
          <>
            <AccountMenu address="alice@vinette.test" {...props} />
            <Where />
          </>
        ),
      },
    ],
    { initialEntries: ['/mail/inbox'] },
  );
  render(<RouterProvider router={router} />);
}

const button = () => screen.getByRole('button', { name: /Меню аккаунта/u });

describe('AccountMenu', () => {
  it('opens on the button: the current account is marked, then Settings and Sign out', async () => {
    const user = userEvent.setup();
    show({ onSignOut: () => undefined });

    await user.click(button());

    const menu = screen.getByRole('menu', { name: 'Аккаунт' });
    expect(button()).toHaveAttribute('aria-expanded', 'true');
    expect(menu).toContainElement(
      screen.getByRole('menuitemradio', { name: /alice@vinette.test/ }),
    );
    expect(screen.getByRole('menuitemradio')).toHaveAttribute('aria-checked', 'true');
    expect(screen.getAllByRole('menuitem').map((i) => i.textContent)).toEqual([
      'Настройки',
      'Выйти',
    ]);
  });

  it('puts the focus on the first item and moves it with the arrow keys, Home and End, round the ends', async () => {
    const user = userEvent.setup();
    show({ onSignOut: () => undefined });

    await user.click(button());
    expect(screen.getByRole('menuitem', { name: 'Настройки' })).toHaveFocus();

    await user.keyboard('{ArrowDown}');
    expect(screen.getByRole('menuitem', { name: 'Выйти' })).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    expect(screen.getByRole('menuitem', { name: 'Настройки' })).toHaveFocus();
    await user.keyboard('{ArrowUp}');
    expect(screen.getByRole('menuitem', { name: 'Выйти' })).toHaveFocus();
    await user.keyboard('{Home}');
    expect(screen.getByRole('menuitem', { name: 'Настройки' })).toHaveFocus();
    await user.keyboard('{End}');
    expect(screen.getByRole('menuitem', { name: 'Выйти' })).toHaveFocus();
  });

  it('closes on Escape and gives the focus back to the button', async () => {
    const user = userEvent.setup();
    show();

    await user.click(button());
    await user.keyboard('{Escape}');

    expect(screen.queryByRole('menu')).toBeNull();
    expect(button()).toHaveFocus();
    expect(button()).toHaveAttribute('aria-expanded', 'false');
  });

  it('closes on a click outside and on the button again', async () => {
    const user = userEvent.setup();
    show();

    await user.click(button());
    await user.click(document.body);
    expect(screen.queryByRole('menu')).toBeNull();

    await user.click(button());
    await user.click(button());
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('signs out from the menu, and closes it', async () => {
    const user = userEvent.setup();
    const onSignOut = vi.fn();
    const onNavigate = vi.fn();
    show({ onSignOut, onNavigate });

    await user.click(button());
    await user.click(screen.getByRole('menuitem', { name: 'Выйти' }));

    expect(onSignOut).toHaveBeenCalledOnce();
    expect(onNavigate).toHaveBeenCalledOnce();
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('leads to the settings, and closes the menu', async () => {
    const user = userEvent.setup();
    show();

    await user.click(button());
    await user.click(screen.getByRole('menuitem', { name: 'Настройки' }));

    expect(screen.getByTestId('where')).toHaveTextContent('/settings');
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('has no "Sign out" when there is no session to end', async () => {
    const user = userEvent.setup();
    show();

    await user.click(button());

    expect(screen.queryByRole('menuitem', { name: 'Выйти' })).toBeNull();
  });

  it('on the rail is a round button with the initials, named by the address, with the same menu', async () => {
    const user = userEvent.setup();
    show({ variant: 'rail', onSignOut: () => undefined });

    expect(button()).toHaveAttribute('title', 'alice@vinette.test');
    expect(button()).toHaveTextContent('A');

    await user.click(button());

    expect(screen.getAllByRole('menuitem')).toHaveLength(2);
  });
});
