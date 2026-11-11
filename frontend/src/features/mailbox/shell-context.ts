import { createContext, useContext } from 'react';
import type { Layout } from './use-layout';

export interface ShellState {
  layout: Layout;
  /** On mobile the folder panel is a drawer; true while it is open. */
  navOpen: boolean;
  openNav: () => void;
  closeNav: () => void;
}

export const ShellContext = createContext<ShellState | null>(null);

export function useShell(): ShellState {
  const state = useContext(ShellContext);
  if (!state) throw new Error('useShell must be used inside AppShell');
  return state;
}
