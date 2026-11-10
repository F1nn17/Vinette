import { createContext, useContext } from 'react';

export const ME_KEY = ['me'] as const;

export type SessionStatus = 'loading' | 'signedIn' | 'signedOut';

export interface SessionValue {
  status: SessionStatus;
  /** The signed-in mailbox address; null unless `status` is `signedIn`. */
  address: string | null;
  /** Why the last session ended, shown on the login screen: the mail provider no longer accepts the saved password. */
  notice: 'sessionRevoked' | null;
  signIn: (address: string, password: string, rememberMe: boolean) => Promise<void>;
  signOut: () => Promise<void>;
}

export const SessionContext = createContext<SessionValue | null>(null);

export function useSession(): SessionValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession must be used inside <SessionProvider>');
  return value;
}
