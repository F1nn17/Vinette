import { expect, type Page } from '@playwright/test';

/** Mailboxes of the dev mail server (`make dev-mail`); the login is the full address. */
export const accounts = {
  alice: { address: 'alice@vinette.test', password: 'alice-pass' },
  bob: { address: 'bob@vinette.test', password: 'bob-pass' },
} as const;

export type Account = (typeof accounts)[keyof typeof accounts];

export const SESSION_COOKIE = '__Host-vinette_session';

/** Waits until the SPA has rendered its first screen (the root is no longer empty). */
export async function waitForAppReady(page: Page): Promise<void> {
  await expect(page.locator('#root > *').first()).toBeVisible();
}

/** Fills the sign-in form and presses the button, without waiting for the outcome. */
export async function submitLogin(
  page: Page,
  address: string,
  password: string,
  options: { rememberMe?: boolean } = {},
): Promise<void> {
  await page.getByLabel('Адрес почты').fill(address);
  await page.getByLabel('Пароль', { exact: true }).fill(password);
  const remember = page.getByRole('checkbox', { name: /Оставаться в системе/ });
  if (options.rememberMe) await remember.check();
  else await remember.uncheck();
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
}

/** Signs in through the login form (docs/design/DESIGN.md section 7) and waits for the mailbox. */
export async function signIn(
  page: Page,
  account: Account,
  options: { rememberMe?: boolean } = {},
): Promise<void> {
  await page.goto('/login');
  await waitForAppReady(page);
  await submitLogin(page, account.address, account.password, options);
  await expect(page).toHaveURL(/\/mail\//);
}

/**
 * Ends the session the way the application does (POST /api/v1/auth/logout with the anti-forgery token).
 * The interface has no sign-out button yet (account menu: VIN-120); then this becomes a click.
 */
export async function signOutViaApi(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
    await fetch('/api/v1/auth/logout', { method: 'POST', headers: { 'X-CSRF-Token': token } });
  });
}
