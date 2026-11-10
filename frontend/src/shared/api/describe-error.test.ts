import { ApiError } from './client';
import { describeApiError } from './describe-error';

const t = (key: string, options?: Record<string, unknown>) =>
  options ? `${key}:${JSON.stringify(options)}` : key;

describe('describeApiError', () => {
  it.each(['mail_unavailable', 'mail_busy', 'session_revoked', 'uid_validity_changed'])(
    'explains %s',
    (code) => {
      expect(describeApiError(new ApiError(503, code, undefined, 'x'), t)).toBe(`errors.${code}`);
    },
  );

  it('turns the wait of a "too many requests" answer into whole minutes, at least one', () => {
    expect(describeApiError(new ApiError(429, 'too_many_requests', undefined, 'x', 90), t)).toBe(
      'errors.too_many_requests:{"minutes":2}',
    );
    expect(describeApiError(new ApiError(429, 'too_many_requests', undefined, 'x', 5), t)).toBe(
      'errors.too_many_requests:{"minutes":1}',
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
