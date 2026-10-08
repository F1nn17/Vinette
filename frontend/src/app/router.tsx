import { createBrowserRouter, Navigate, type RouteObject } from 'react-router';
import { ComposePage } from './routes/ComposePage';
import { LoginPage } from './routes/LoginPage';
import { MailboxPage } from './routes/MailboxPage';

const routes: RouteObject[] = [
  { path: '/', element: <Navigate to="/mail/inbox" replace /> },
  { path: '/login', element: <LoginPage /> },
  { path: '/mail/:folder', element: <MailboxPage /> },
  { path: '/compose', element: <ComposePage /> },
];

// The primitives catalogue exists only in dev builds; it is stripped from production bundles.
if (import.meta.env.DEV) {
  routes.push({
    path: '/dev/ui',
    lazy: async () => ({ Component: (await import('./routes/DevUiPage')).DevUiPage }),
  });
}

export const router = createBrowserRouter(routes);
