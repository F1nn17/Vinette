import { expect, test } from '@playwright/test';

test('login page opens', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
});
