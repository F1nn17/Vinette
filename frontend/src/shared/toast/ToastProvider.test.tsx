import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  MAX_TOASTS,
  TOAST_DURATION_MS,
  TOAST_ERROR_DURATION_MS,
  useToast,
  type ToastOptions,
} from './toast-context';
import { ToastProvider } from './ToastProvider';

function Trigger({ options }: { options: ToastOptions }) {
  const toast = useToast();
  return <button onClick={() => toast.show(options)}>show</button>;
}

function mount(options: ToastOptions) {
  render(
    <ToastProvider>
      <Trigger options={options} />
    </ToastProvider>,
  );
}

const setup = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
afterEach(() => vi.useRealTimers());

describe('ToastProvider', () => {
  it('shows a toast and removes it after the default time', async () => {
    mount({ message: 'Готово' });
    await setup().click(screen.getByText('show'));
    expect(screen.getByRole('status')).toHaveTextContent('Готово');

    act(() => void vi.advanceTimersByTime(TOAST_DURATION_MS + 10));

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('keeps an error longer than an ordinary message', async () => {
    mount({ message: 'Не получилось', tone: 'error' });
    await setup().click(screen.getByText('show'));

    act(() => void vi.advanceTimersByTime(TOAST_DURATION_MS + 10));
    expect(screen.getByRole('alert')).toBeInTheDocument();

    act(() => void vi.advanceTimersByTime(TOAST_ERROR_DURATION_MS - TOAST_DURATION_MS));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('waits while the pointer is on it and goes on when it leaves', async () => {
    mount({ message: 'Готово' });
    const user = setup();
    await user.click(screen.getByText('show'));

    await user.hover(screen.getByRole('status'));
    act(() => void vi.advanceTimersByTime(TOAST_DURATION_MS * 3));
    expect(screen.getByRole('status')).toBeInTheDocument();

    await user.unhover(screen.getByRole('status'));
    act(() => void vi.advanceTimersByTime(TOAST_DURATION_MS + 10));
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('closes with its cross', async () => {
    mount({ message: 'Готово' });
    const user = setup();
    await user.click(screen.getByText('show'));

    await user.click(screen.getByRole('button', { name: 'Закрыть' }));

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('runs the action and closes the toast', async () => {
    const onSelect = vi.fn();
    mount({ message: 'Письмо перенесено', action: { label: 'Отменить', onSelect } });
    const user = setup();
    await user.click(screen.getByText('show'));

    await user.click(screen.getByRole('button', { name: 'Отменить' }));

    expect(onSelect).toHaveBeenCalledOnce();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('keeps a toast until it is closed when its time is 0', async () => {
    mount({ message: 'Остаётся', duration: 0 });
    await setup().click(screen.getByText('show'));

    act(() => void vi.advanceTimersByTime(60_000));

    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it(`shows at most ${MAX_TOASTS}: the oldest goes first`, async () => {
    function Many() {
      const toast = useToast();
      return (
        <button
          onClick={() => {
            for (const m of ['один', 'два', 'три', 'четыре']) {
              toast.show({ message: m, duration: 0 });
            }
          }}
        >
          show
        </button>
      );
    }
    render(
      <ToastProvider>
        <Many />
      </ToastProvider>,
    );

    await setup().click(screen.getByText('show'));

    expect(screen.getAllByRole('status').map((n) => n.querySelector('p')?.textContent)).toEqual([
      'два',
      'три',
      'четыре',
    ]);
  });

  it('has to be used inside the provider', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    expect(() => render(<Trigger options={{ message: 'x' }} />)).toThrow(/ToastProvider/);
    spy.mockRestore();
  });
});
