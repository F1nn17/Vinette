import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@/shared/api/client';
import { ListEmpty, ListError, ListNoFolder, ListSkeleton, OfflineBanner } from './list-states';
import { useOnline } from './use-online';

describe('list states', () => {
  it('the skeleton is a labelled status with placeholder rows', () => {
    render(<ListSkeleton rows={3} />);

    expect(screen.getByRole('status', { name: 'Загрузка…' })).toBeInTheDocument();
    expect(screen.getAllByTestId('skeleton-row')).toHaveLength(3);
  });

  it('the empty state tells to try another filter', () => {
    render(<ListEmpty />);

    expect(screen.getByText('Здесь пусто. Попробуйте другой фильтр.')).toBeInTheDocument();
  });

  it('the missing folder is said in plain words', () => {
    render(<ListNoFolder />);

    expect(screen.getByText('Такой папки нет.')).toBeInTheDocument();
  });

  it('the error explains what happened without technical words and retries on the button', async () => {
    const onRetry = vi.fn();
    render(
      <ListError error={new ApiError(503, 'mail_unavailable', undefined, 'x')} onRetry={onRetry} />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Почта временно недоступна');
    await userEvent.click(screen.getByRole('button', { name: 'Повторить' }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('a network failure is explained too', () => {
    render(<ListError error={new TypeError('Failed to fetch')} onRetry={() => undefined} />);

    expect(screen.getByRole('alert')).toHaveTextContent('Нет связи');
  });

  it('the offline banner is a status message', () => {
    render(<OfflineBanner />);

    expect(screen.getByRole('status')).toHaveTextContent('Нет связи');
  });
});

describe('useOnline', () => {
  function Probe() {
    return <p>{useOnline() ? 'online' : 'offline'}</p>;
  }

  it('follows the browser going offline and coming back', () => {
    const online = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true);
    render(<Probe />);
    expect(screen.getByText('online')).toBeInTheDocument();

    online.mockReturnValue(false);
    act(() => void window.dispatchEvent(new Event('offline')));
    expect(screen.getByText('offline')).toBeInTheDocument();

    online.mockReturnValue(true);
    act(() => void window.dispatchEvent(new Event('online')));
    expect(screen.getByText('online')).toBeInTheDocument();

    online.mockRestore();
  });
});
