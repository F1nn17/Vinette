import type { TFunction } from 'i18next';
import { ApiError } from './client';
import { describeApiError } from './describe-error';

// A stand-in that shows the key and the values it was given (i18next's own function is not needed to check which text is chosen).
const t = ((key: string, options?: Record<string, unknown>) =>
  options ? `${key}:${JSON.stringify(options)}` : key) as unknown as TFunction;

describe('describeApiError', () => {
  it.each([
    'mail_unavailable',
    'mail_busy',
    'session_revoked',
    'uid_validity_changed',
    'not_found',
    'payload_too_large',
  ])('explains %s', (code) => {
    expect(describeApiError(new ApiError(503, code, undefined, 'x'), t)).toBe(`errors.${code}`);
  });

  it('turns the wait of a "too many requests" answer into whole minutes, at least one', () => {
    expect(describeApiError(new ApiError(429, 'too_many_requests', undefined, 'x', 90), t)).toBe(
      'errors.too_many_requests:{"minutes":2}',
    );
    expect(describeApiError(new ApiError(429, 'too_many_requests', undefined, 'x', 5), t)).toBe(
      'errors.too_many_requests:{"minutes":1}',
    );
  });

  it('tells how long to wait when too many messages were sent: minutes for a short wait, hours for a long one', () => {
    const wait = (seconds?: number) =>
      describeApiError(new ApiError(429, 'send_limit_reached', undefined, 'x', seconds), t);

    expect(wait(1500)).toBe('errors.send_limit_reached_minutes:{"minutes":25}');
    expect(wait(10)).toBe('errors.send_limit_reached_minutes:{"minutes":1}');
    expect(wait(3 * 3600 + 5)).toBe('errors.send_limit_reached_hours:{"hours":4}');
    expect(wait(undefined)).toBe('errors.send_limit_reached_minutes:{"minutes":60}');
  });

  it('explains the limits on attached files', () => {
    for (const code of ['attachment_too_large', 'attachments_too_large', 'too_many_attachments']) {
      expect(describeApiError(new ApiError(413, code, undefined, 'x'), t)).toBe(`errors.${code}`);
    }
  });

  it('explains why a message could not be sent, naming the address the server refused', () => {
    for (const code of ['send_refused', 'send_too_large', 'send_uncertain']) {
      expect(describeApiError(new ApiError(422, code, undefined, 'x'), t)).toBe(`errors.${code}`);
    }
    expect(
      describeApiError(
        new ApiError(422, 'recipient_rejected', undefined, 'x', undefined, 'nobody@example.org'),
        t,
      ),
    ).toBe('errors.recipient_rejected_named:{"address":"nobody@example.org"}');
    expect(describeApiError(new ApiError(422, 'recipient_rejected', undefined, 'x'), t)).toBe(
      'errors.recipient_rejected',
    );
  });

  it('says there is no connection when the browser could not reach the application', () => {
    expect(describeApiError(new TypeError('Failed to fetch'), t)).toBe('errors.network');
  });

  it('falls back to a general text and never shows the technical title', () => {
    expect(describeApiError(new ApiError(500, 'internal_error', undefined, 'Boom'), t)).toBe(
      'errors.unknown',
    );
    expect(describeApiError(new Error('x'), t)).toBe('errors.unknown');
  });
});
