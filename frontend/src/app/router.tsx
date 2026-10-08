import { createBrowserRouter, Navigate } from 'react-router';
import { ComposePage } from './routes/ComposePage';
import { LoginPage } from './routes/LoginPage';
import { MailboxPage } from './routes/MailboxPage';

export const router = createBrowserRouter([
  { path: '/', element: <Navigate to="/mail/inbox" replace /> },
  { path: '/login', element: <LoginPage /> },
  { path: '/mail/:folder', element: <MailboxPage /> },
  { path: '/compose', element: <ComposePage /> },
]);
