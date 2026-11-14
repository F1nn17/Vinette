import { HttpTransportType } from '@microsoft/signalr';
import { HUB_OPTIONS, HUB_URL, isRefusal, retryDelay } from './connection';

describe('isRefusal', () => {
  it('is true for a refused session (401 or 403) however the library words it', () => {
    expect(
      isRefusal(
        new Error("Failed to complete negotiation: Error: Unauthorized: Status code '401'"),
      ),
    ).toBe(true);
    expect(isRefusal(new Error("Status code '403'"))).toBe(true);
    expect(isRefusal("Status code '401'")).toBe(true);
  });

  it('is false for anything that may pass: no connection, a busy server, a number that only looks like a code', () => {
    expect(isRefusal(new Error('Failed to fetch'))).toBe(false);
    expect(isRefusal(new Error("Status code '503'"))).toBe(false);
    expect(isRefusal(new Error('after 14010 ms'))).toBe(false);
    expect(isRefusal(undefined)).toBe(false);
  });
});

describe('retryDelay', () => {
  const middle = () => 0.5; // no spread

  it('doubles from a second and stops growing at a minute', () => {
    expect([0, 1, 2, 3, 4, 5, 6, 7, 20].map((n) => retryDelay(n, middle))).toEqual([
      1000, 2000, 4000, 8000, 16000, 32000, 60000, 60000, 60000,
    ]);
  });

  it('spreads every wait by at most 20 percent either way, so that browsers do not come back together', () => {
    expect(retryDelay(2, () => 0)).toBe(3200);
    expect(retryDelay(2, () => 1)).toBe(4800);
  });
});

describe('the connection', () => {
  it('is a plain WebSocket, with no negotiation request, so that no instance of the application needs to be the same one twice', () => {
    expect(HUB_OPTIONS.skipNegotiation).toBe(true);
    expect(HUB_OPTIONS.transport).toBe(HttpTransportType.WebSockets);
    expect(HUB_URL).toBe('/hubs/mail');
  });
});
