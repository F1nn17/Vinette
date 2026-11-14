import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
  type IRetryPolicy,
  type RetryContext,
} from '@microsoft/signalr';

/** The address of the real-time channel (the same origin as the application; the session cookie goes with it). */
export const HUB_URL = '/hubs/mail';

/** What the application uses of a connection (a smaller surface than `HubConnection`, so that tests can stand in for it). */
export type HubLike = Pick<
  HubConnection,
  'start' | 'stop' | 'on' | 'off' | 'onreconnecting' | 'onreconnected' | 'onclose' | 'state'
>;

export type RealtimeStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** A refusal of the session: trying again would only be refused again (the user is signed out by the next request). */
export function isRefusal(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');
  return /\b(401|403)\b/.test(message);
}

/** How long to wait before try number `attempt` (0 = the first): 1 s, 2 s, 4 s ... at most a minute, spread by up to 20 %. */
export function retryDelay(attempt: number, random: () => number = Math.random): number {
  const base = Math.min(60_000, 1000 * 2 ** Math.min(attempt, 10));
  return Math.round(base * (1 + (random() * 2 - 1) * 0.2));
}

/** Tries again for as long as the connection is wanted, with growing pauses; stops when the session is refused. */
const retryPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds: (context: RetryContext) =>
    isRefusal(context.retryReason) ? null : retryDelay(context.previousRetryCount),
};

/**
 * WebSockets only, and without the negotiation request (VIN-088): the negotiation and the connection that follows it would have to
 * reach the same instance of the application, which means "sticky" routing in the load balancer; a plain WebSocket needs none, so
 * any instance can take it. The price: a proxy that does not pass WebSocket upgrades leaves the page without live updates (the
 * notice "No connection" says so), where the library's fallbacks would have used long polling.
 */
export const HUB_OPTIONS = {
  skipNegotiation: true,
  transport: HttpTransportType.WebSockets,
} as const;

export function createHubConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(HUB_URL, HUB_OPTIONS)
    .withAutomaticReconnect(retryPolicy)
    .configureLogging(LogLevel.None) // nothing of the channel goes to the console
    .build();
}

export { HubConnectionState };
