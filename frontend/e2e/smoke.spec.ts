import { expect, test } from '@playwright/test';
import { waitForAppReady } from './helpers';

test('login page opens', async ({ page }) => {
  await page.goto('/login');
  await waitForAppReady(page);

  await expect(page.getByRole('heading', { level: 1 })).toContainText('Вход');
});

test('a signed-out visitor of a protected page is sent to the login screen', async ({ page }) => {
  await page.goto('/mail/inbox');
  await waitForAppReady(page);

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Вход');
});

test('API is reachable through the dev proxy', async ({ request }) => {
  const response = await request.get('/api/v1/health');

  expect(response.ok()).toBe(true);
  expect(await response.json()).toMatchObject({
    status: 'ok',
    version: expect.stringMatching(/^\d+\.\d+\.\d+/),
  });
});

test('unknown API routes return problem+json with a correlation id', async ({ request }) => {
  const response = await request.get('/api/v1/definitely-not-here');

  expect(response.status()).toBe(404);
  expect(response.headers()['content-type']).toContain('application/problem+json');
  expect(response.headers()['x-correlation-id']).toBeTruthy();
  expect(await response.json()).toMatchObject({ code: 'not_found' });
});
