import { useTranslation } from 'react-i18next';
import { Navigate, Outlet, useLocation } from 'react-router';
import { useSession } from './session-context';

export const DEFAULT_AFTER_LOGIN = '/mail/inbox';

/** Only paths inside this application: "//host" and "/\host" would leave it. */
function isLocalPath(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.startsWith('/') &&
    !value.startsWith('//') &&
    !value.includes('\\')
  );
}

function Loading() {
  const { t } = useTranslation();
  return (
    <div role="status" className="p-8 text-muted">
      {t('common.loading')}
    </div>
  );
}

/** Wraps the routes that need a signed-in user; everyone else is sent to the login screen (and back afterwards). */
export function RequireAuth() {
  const { status } = useSession();
  const location = useLocation();

  if (status === 'loading') return <Loading />;
  if (status === 'signedOut') {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }
  return <Outlet />;
}

/** Wraps the login screen: a user who is already signed in has nothing to do there. */
export function RedirectIfSignedIn() {
  const { status } = useSession();
  const location = useLocation();

  if (status === 'loading') return <Loading />;
  if (status === 'signedIn') {
    const from = (location.state as { from?: unknown } | null)?.from;
    return <Navigate to={isLocalPath(from) ? from : DEFAULT_AFTER_LOGIN} replace />;
  }
  return <Outlet />;
}
