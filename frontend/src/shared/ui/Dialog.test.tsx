import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { Dialog } from './Dialog';

function Harness({ initiallyOpen = true }: { initiallyOpen?: boolean }) {
  const [open, setOpen] = useState(initiallyOpen);
  return (
    <>
      <button onClick={() => setOpen(true)}>opener</button>
      <Dialog
        open={open}
        onClose={() => setOpen(false)}
        title="Горячие клавиши"
        closeLabel="Закрыть"
      >
        <button>first</button>
        <button>last</button>
      </Dialog>
    </>
  );
}

describe('Dialog', () => {
  it('is a modal dialog with a name', () => {
    render(<Harness />);

    const dialog = screen.getByRole('dialog', { name: 'Горячие клавиши' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  it('renders nothing while closed', () => {
    render(<Harness initiallyOpen={false} />);

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('puts the focus inside, on its first control', () => {
    render(<Harness />);

    expect(screen.getByRole('button', { name: 'Закрыть' })).toHaveFocus();
  });

  it('closes on Escape, with its cross and on a click on the dimmed part', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(screen.getByText('opener'));
    await user.click(screen.getByRole('button', { name: 'Закрыть' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(screen.getByText('opener'));
    await user.click(screen.getByRole('dialog').parentElement as HTMLElement);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('does not close on a click inside the card', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(screen.getByText('first'));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('keeps Tab inside: after the last control comes the first, and Shift+Tab goes round the other way', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const cross = screen.getByRole('button', { name: 'Закрыть' });
    const last = screen.getByText('last');

    await user.tab();
    await user.tab();
    expect(last).toHaveFocus();
    await user.tab();
    expect(cross).toHaveFocus();
    await user.tab({ shift: true });
    expect(last).toHaveFocus();
  });

  it('gives the focus back to where it was when it closes', async () => {
    const user = userEvent.setup();
    render(<Harness initiallyOpen={false} />);
    const opener = screen.getByText('opener');
    await user.click(opener);
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await user.keyboard('{Escape}');

    expect(opener).toHaveFocus();
  });
});
