import { Navigate, type RouteObject } from 'react-router';
import { RedirectIfSignedIn, RequireAuth } from '@/features/auth';
import { ComposePage } from './routes/ComposePage';
import { LoginPage } from './routes/LoginPage';
import { MailboxPage } from './routes/MailboxPage';

export const appRoutes: RouteObject[] = [
  { path: '/', element: <Navigate to="/mail/inbox" replace /> },
  {
    element: <RedirectIfSignedIn />,
    children: [{ path: '/login', element: <LoginPage /> }],
  },
  {
    element: <RequireAuth />,
    children: [
      { path: '/mail/:folder', element: <MailboxPage /> },
      { path: '/compose', element: <ComposePage /> },
    ],
  },
];

// The primitives catalogue exists only in dev builds; it is stripped from production bundles.
if (import.meta.env.DEV) {
  appRoutes.push({
    path: '/dev/ui',
    lazy: async () => ({ Component: (await import('./routes/DevUiPage')).DevUiPage }),
  });
}
