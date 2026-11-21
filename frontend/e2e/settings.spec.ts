import { expect, test } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// The settings screen, end to end: the real backend (the settings are kept on the server) and a real Dovecot. Alice's settings are changed
// and put back at the end, as the other scenarios share her mailbox.

test.describe('settings', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.alice);
  });

  test.afterEach(async ({ page }) => {
    // back to the defaults: comfortable, language of the browser
    await page.evaluate(async () => {
      const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
      await fetch('/api/v1/settings', {
        method: 'PATCH',
        headers: { 'X-CSRF-Token': token, 'Content-Type': 'application/json' },
        body: JSON.stringify({ density: 'comfortable', language: '' }),
      });
    });
    await signOutViaApi(page);
  });

  test('the address block opens the settings; the density is saved on the server and survives a reload', async ({
    page,
  }) => {
    await page
      .getByRole('link', { name: /Настройки/ })
      .first()
      .click();
    await expect(page).toHaveURL(/\/settings$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Настройки' })).toBeVisible();

    // the change is shown at once; the reload below must come after the server has it
    const saved = page.waitForResponse(
      (r) => r.url().endsWith('/api/v1/settings') && r.request().method() === 'PATCH',
    );
    await page.getByRole('button', { name: 'Компактная' }).click();
    await saved;
    await expect(page.getByRole('button', { name: 'Компактная' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await page.reload();
    await expect(page.getByRole('button', { name: 'Компактная' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    const density = await page.evaluate(async () => {
      const response = await fetch('/api/v1/settings');
      return ((await response.json()) as { density: string }).density;
    });
    expect(density).toBe('compact');
  });

  test('the language changes at once and is saved', async ({ page }) => {
    await page.goto('/settings');
    await expect(page.getByRole('heading', { level: 1, name: 'Настройки' })).toBeVisible();

    await page.getByRole('combobox', { name: 'Язык' }).selectOption('en');

    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Language' })).toHaveValue('en');
    const saved = await page.evaluate(async () => {
      const response = await fetch('/api/v1/settings');
      return ((await response.json()) as { language: string | null }).language;
    });
    expect(saved).toBe('en');
  });

  test('the signatures and the trusted senders have their sections', async ({ page }) => {
    await page.goto('/settings');
    await expect(page.getByRole('heading', { level: 2, name: 'Подписи' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 2, name: 'Картинки в письмах' })).toBeVisible();
  });
});
