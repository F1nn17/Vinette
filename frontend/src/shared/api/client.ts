import type { paths } from './schema';

type HealthResponse =
  paths['/api/v1/health']['get']['responses']['200']['content']['application/json'];

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { credentials: 'same-origin', signal });
  if (!response.ok) {
    throw new Error(`Request failed: ${response.status}`);
  }
  return (await response.json()) as T;
}

export const getHealth = (signal?: AbortSignal) => apiGet<HealthResponse>('/api/v1/health', signal);
