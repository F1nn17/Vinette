import { createContext, useContext } from 'react';
import type { RealtimeStatus } from './connection';

export const RealtimeContext = createContext<RealtimeStatus>('disconnected');

/** Whether the real-time channel is up (for the "no connection" notice, VIN-085). */
export function useRealtimeStatus(): RealtimeStatus {
  return useContext(RealtimeContext);
}
