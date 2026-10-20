import { expect, type Page } from '@playwright/test';

/** Mailboxes of the dev mail server (`make dev-mail`); the login is the full address. */
export const accounts = {
  alice: { address: 'alice@vinette.test', password: 'alice-pass' },
  bob: { address: 'bob@vinette.test', password: 'bob-pass' },
} as const;

export type Account = (typeof accounts)[keyof typeof accounts];

/** Waits until the SPA has rendered its first screen (the root is no longer empty). */
export async function waitForAppReady(page: Page): Promise<void> {
  await expect(page.locator('#root > *').first()).toBeVisible();
}

/**
 * Signs in through the login form (labels and button text come from docs/design/DESIGN.md §7).
 * The form is implemented in VIN-026; until then this helper is unused.
 */
export async function signIn(page: Page, account: Account): Promise<void> {
  await page.goto('/login');
  await waitForAppReady(page);
  await page.getByLabel('Адрес почты').fill(account.address);
  await page.getByLabel('Пароль', { exact: true }).fill(account.password);
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page).toHaveURL(/\/mail\//);
}
