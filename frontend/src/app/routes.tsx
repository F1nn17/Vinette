import { Navigate, type RouteObject } from 'react-router';
import { RedirectIfSignedIn, RequireAuth } from '@/features/auth';
import { LoginPage } from './routes/LoginPage';
import { MailboxPage } from './routes/MailboxPage';
import { RealtimeLayout } from './routes/RealtimeLayout';
import { ScheduledPage } from './routes/ScheduledPage';

export const appRoutes: RouteObject[] = [
  { path: '/', element: <Navigate to="/mail/inbox" replace /> },
  {
    element: <RedirectIfSignedIn />,
    children: [{ path: '/login', element: <LoginPage /> }],
  },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <RealtimeLayout />,
        children: [
          // One route for the list and for an open message: the screen is not built again when a message is opened, so the
          // list keeps what it loaded and where it was scrolled to.
          { path: '/mail/:folder/:messageId?', element: <MailboxPage /> },
          // The messages of a label from all folders. `@label` cannot be a folder: the folder in the address is always encoded.
          { path: '/mail/@label/:name/:messageId?', element: <MailboxPage /> },
          // The messages put off with "Send later": the queue of the application, not a folder of the mailbox.
          { path: '/mail/@scheduled/:id?', element: <ScheduledPage /> },
          // The editor is heavy and only the compose screen needs it: it is loaded when the screen is first opened.
          {
            path: '/compose',
            lazy: async () => ({ Component: (await import('./routes/ComposePage')).ComposePage }),
          },
        ],
      },
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
