import { ApiError } from './client';

type Translate = (key: string, options?: Record<string, unknown>) => string;

/**
 * A text for the user for a failed API call (no technical wording, see docs/design/DESIGN.md). Codes are listed in
 * docs/api-errors.md; the ones about the mail connection are explained here for every screen that talks to the mail.
 */
export function describeApiError(error: unknown, t: Translate): string {
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'mail_unavailable':
      case 'mail_busy':
      case 'session_revoked':
      case 'uid_validity_changed':
        return t(`errors.${error.code}`);
      case 'too_many_requests': {
        const minutes = Math.max(1, Math.ceil((error.retryAfterSeconds ?? 60) / 60));
        return t('errors.too_many_requests', { minutes });
      }
    }
  }
  // fetch rejects with a TypeError when the browser cannot reach the application at all.
  if (error instanceof TypeError) return t('errors.network');
  return t('errors.unknown');
}
