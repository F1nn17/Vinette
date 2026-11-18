import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { ToastProvider } from '@/shared/toast';
import { ComposeScreen } from './ComposeScreen';

describe('ComposeScreen', () => {
  it('has the header with the way back and the form', () => {
    const router = createMemoryRouter(
      [{ path: '/compose', element: <ComposeScreen address="user@example.com" /> }],
      {
        initialEntries: ['/compose'],
      },
    );
    render(
      <QueryClientProvider client={new QueryClient()}>
        <ToastProvider>
          <RouterProvider router={router} />
        </ToastProvider>
      </QueryClientProvider>,
    );

    expect(screen.getByRole('heading', { level: 1, name: 'Новое письмо' })).toBeInTheDocument();
    const back = screen.getByRole('link', { name: 'Назад во Входящие' });
    expect(back).toHaveAttribute('href', '/mail/inbox');
    expect(screen.getByRole('main')).toContainElement(screen.getByRole('form', { name: 'Письмо' }));
    expect(screen.getByLabelText('От')).toHaveValue('user@example.com');
  });

  it('shows when the draft was saved, in the header', async () => {
    const json = (body: unknown) =>
      new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
    vi.stubGlobal(
      'fetch',
      vi.fn((input: string) =>
        Promise.resolve(
          input === '/api/v1/csrf'
            ? json({ token: 't' })
            : input === '/api/v1/attachments'
              ? json({ items: [], limits: { maxFileBytes: 1, maxTotalBytes: 1, maxFiles: 1 } })
              : json({
                  id: 'd1',
                  messageId: 'm1',
                  savedAt: '2026-11-18T14:41:00Z',
                  previousRemoved: true,
                }),
        ),
      ),
    );
    const router = createMemoryRouter(
      [{ path: '/compose', element: <ComposeScreen address="user@example.com" /> }],
      { initialEntries: ['/compose'] },
    );
    render(
      <QueryClientProvider client={new QueryClient()}>
        <ToastProvider>
          <RouterProvider router={router} />
        </ToastProvider>
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByLabelText('Тема'), 'Hi');

    expect(
      await screen.findByText(/Черновик сохранён в \d/, {}, { timeout: 4000 }),
    ).toBeInTheDocument();
    vi.unstubAllGlobals();
  });
});
