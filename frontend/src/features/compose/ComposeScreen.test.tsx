import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
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
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    expect(screen.getByRole('heading', { level: 1, name: 'Новое письмо' })).toBeInTheDocument();
    const back = screen.getByRole('link', { name: 'Назад во Входящие' });
    expect(back).toHaveAttribute('href', '/mail/inbox');
    expect(screen.getByRole('main')).toContainElement(screen.getByRole('form', { name: 'Письмо' }));
    expect(screen.getByLabelText('От')).toHaveValue('user@example.com');
  });
});
