import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { LabelMenu } from './LabelMenu';
import type { LabelState } from './label-state';

const LABELS = [
  { name: 'Work', color: 'work' },
  { name: 'Счета', color: null },
  { name: 'Home', color: null },
];

function setup(states: Record<string, LabelState> = {}, labels = LABELS) {
  const onToggle = vi.fn();
  const onCreate = vi.fn();
  const onClose = vi.fn();
  render(
    <div>
      <button type="button" data-labels-trigger>
        trigger
      </button>
      <p>outside</p>
      <LabelMenu
        labels={labels}
        stateOf={(name) => states[name] ?? 'none'}
        onToggle={onToggle}
        onCreate={onCreate}
        onClose={onClose}
      />
    </div>,
  );
  return { onToggle, onCreate, onClose };
}

describe('LabelMenu', () => {
  it('shows a box for every label: full, half or empty', () => {
    setup({ Work: 'all', Счета: 'some' });

    expect(screen.getByRole('checkbox', { name: 'Work' })).toBeChecked();
    const half = screen.getByRole<HTMLInputElement>('checkbox', { name: 'Счета' });
    expect(half.indeterminate).toBe(true);
    expect(half).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Home' })).not.toBeChecked();
  });

  it('puts the label on when the box is empty or half full, and takes it off when it is full', async () => {
    const user = userEvent.setup();
    const { onToggle } = setup({ Work: 'all', Счета: 'some' });

    await user.click(screen.getByRole('checkbox', { name: 'Work' }));
    await user.click(screen.getByRole('checkbox', { name: 'Счета' }));
    await user.click(screen.getByRole('checkbox', { name: 'Home' }));

    expect(onToggle.mock.calls).toEqual([
      ['Work', false],
      ['Счета', true],
      ['Home', true],
    ]);
  });

  it('puts the focus on the first box when it opens', () => {
    setup();

    expect(screen.getByRole('checkbox', { name: 'Work' })).toHaveFocus();
  });

  it('with no labels says so and puts the focus into the field for a new one', () => {
    setup({}, []);

    expect(screen.getByText(/Меток пока нет/u)).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Название новой метки' })).toHaveFocus();
  });

  it('makes a new label from the typed name, without the spaces around it, and empties the field', async () => {
    const user = userEvent.setup();
    const { onCreate } = setup();
    const field = screen.getByRole('textbox', { name: 'Название новой метки' });

    await user.type(field, '  Отпуск 2026  {Enter}');

    expect(onCreate).toHaveBeenCalledExactlyOnceWith('Отпуск 2026');
    expect(field).toHaveValue('');
  });

  it('does not make a label from an empty name', async () => {
    const user = userEvent.setup();
    const { onCreate } = setup();

    await user.type(screen.getByRole('textbox', { name: 'Название новой метки' }), '   {Enter}');

    expect(onCreate).not.toHaveBeenCalled();
  });

  it('closes on Escape and on a click outside, but not on a click on its own button', async () => {
    const user = userEvent.setup();
    const { onClose } = setup();

    await user.click(screen.getByRole('button', { name: 'trigger' }));
    expect(onClose).not.toHaveBeenCalled();

    await user.click(screen.getByText('outside'));
    expect(onClose).toHaveBeenCalledTimes(1);

    await user.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('does not close when a box inside it is clicked', async () => {
    const user = userEvent.setup();
    const { onClose } = setup();

    await user.click(screen.getByRole('checkbox', { name: 'Home' }));

    expect(onClose).not.toHaveBeenCalled();
  });
});
