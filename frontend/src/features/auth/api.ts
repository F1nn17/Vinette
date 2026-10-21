import { ApiError, apiGet, apiPost } from '@/shared/api/client';
import type { components } from '@/shared/api/schema';

export type Me = components['schemas']['MeResponse'];
export type LoginBody = components['schemas']['LoginRequestBody'];

/** The signed-in mailbox, or null when there is no (valid) session. */
export async function fetchMe(signal?: AbortSignal): Promise<Me | null> {
  try {
    return await apiGet<Me>('/api/v1/me', signal);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) return null;
    throw error;
  }
}

export const login = (body: LoginBody) => apiPost<Me>('/api/v1/auth/login', body);

export const logout = () => apiPost<void>('/api/v1/auth/logout');
