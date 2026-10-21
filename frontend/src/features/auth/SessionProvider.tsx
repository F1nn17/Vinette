import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, type ReactNode } from 'react';
import { onUnauthorized } from '@/shared/api/client';
import { fetchMe, login, logout, type Me } from './api';
import { ME_KEY, SessionContext, type SessionValue } from './session-context';

/**
 * Knows who is signed in. The server is the source of truth (`GET /api/v1/me`); a 401 from any API call means the
 * session ended, which signs the user out here and drops everything cached for them.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();

  const { data, isPending } = useQuery<Me | null>({
    queryKey: ME_KEY,
    queryFn: ({ signal }) => fetchMe(signal),
    retry: false,
    staleTime: 5 * 60_000,
  });

  /** Forgets the user and every query cached while they were signed in. */
  const endSession = useCallback(() => {
    queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== ME_KEY[0] });
    queryClient.setQueryData<Me | null>(ME_KEY, null);
  }, [queryClient]);

  useEffect(() => onUnauthorized(endSession), [endSession]);

  const signIn = useCallback(
    async (address: string, password: string, rememberMe: boolean) => {
      const me = await login({ address, password, rememberMe });
      queryClient.setQueryData<Me | null>(ME_KEY, me);
    },
    [queryClient],
  );

  const signOut = useCallback(async () => {
    try {
      await logout();
    } finally {
      endSession(); // even if the request failed, this browser must not keep acting as the user
    }
  }, [endSession]);

  const value = useMemo<SessionValue>(
    () => ({
      status: isPending ? 'loading' : data ? 'signedIn' : 'signedOut',
      address: data?.address ?? null,
      signIn,
      signOut,
    }),
    [data, isPending, signIn, signOut],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
