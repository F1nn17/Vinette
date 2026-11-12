import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Toast } from './Toast';

describe('Toast', () => {
  it('announces an error at once and an ordinary message politely', () => {
    const { rerender } = render(
      <Toast tone="error" closeLabel="Закрыть" onClose={() => undefined}>
        Не получилось
      </Toast>,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('Не получилось');

    rerender(
      <Toast closeLabel="Закрыть" onClose={() => undefined}>
        Готово
      </Toast>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('Готово');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('closes with its cross, which has a name', async () => {
    const onClose = vi.fn();
    render(
      <Toast closeLabel="Закрыть" onClose={onClose}>
        Готово
      </Toast>,
    );

    await userEvent.setup().click(screen.getByRole('button', { name: 'Закрыть' }));

    expect(onClose).toHaveBeenCalledOnce();
  });

  it('has an action when it has both a label and a handler', async () => {
    const onAction = vi.fn();
    render(
      <Toast
        closeLabel="Закрыть"
        onClose={() => undefined}
        actionLabel="Отменить"
        onAction={onAction}
      >
        Письмо перенесено
      </Toast>,
    );

    await userEvent.setup().click(screen.getByRole('button', { name: 'Отменить' }));

    expect(onAction).toHaveBeenCalledOnce();
  });

  it('shows no action button without a handler', () => {
    render(
      <Toast closeLabel="Закрыть" onClose={() => undefined} actionLabel="Отменить">
        Письмо перенесено
      </Toast>,
    );

    expect(screen.queryByRole('button', { name: 'Отменить' })).not.toBeInTheDocument();
  });
});
