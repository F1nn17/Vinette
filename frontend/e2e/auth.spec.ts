import { expect, test } from '@playwright/test';
import {
  accounts,
  SESSION_COOKIE,
  signIn,
  signOutViaApi,
  submitLogin,
  waitForAppReady,
} from './helpers';

// Runs against the real stack: the backend talks to the dev mail server (`make dev-mail`).
// Dovecot slows down every failed login (2 s, growing to ~15 s for one address), hence the patient timeouts below.
const SLOW_REJECTION = { timeout: 45_000 };

test.describe('sign-in', () => {
  test('signing in opens the mailbox and the session survives a reload', async ({ page }) => {
    await page.goto('/mail/inbox');
    await waitForAppReady(page);
    await expect(page).toHaveURL(/\/login$/); // not signed in: sent to the login screen

    await submitLogin(page, accounts.alice.address, accounts.alice.password);

    await expect(page).toHaveURL(/\/mail\/inbox$/); // back to the page that was asked for
    await expect(page.getByRole('heading', { level: 1, name: 'Почта' })).toBeVisible();

    await page.reload();
    await waitForAppReady(page);

    await expect(page).toHaveURL(/\/mail\/inbox$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Почта' })).toBeVisible();
  });

  test('the session cookie is not readable by scripts', async ({ page }) => {
    await signIn(page, accounts.alice);

    const cookie = (await page.context().cookies()).find((c) => c.name === SESSION_COOKIE);

    expect(cookie).toBeDefined();
    expect(cookie).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict' });
    expect(await page.evaluate(() => document.cookie)).not.toContain('vinette_session');
  });

  test('a wrong password shows a plain message and keeps the user on the login screen', async ({
    page,
  }) => {
    await page.goto('/login');
    await waitForAppReady(page);

    await submitLogin(page, accounts.alice.address, 'not-the-password');

    await expect(page.getByRole('alert')).toContainText(
      'Неверный адрес или пароль',
      SLOW_REJECTION,
    );
    await expect(page).toHaveURL(/\/login$/);
    expect((await page.context().cookies()).some((c) => c.name === SESSION_COOKIE)).toBe(false);
  });

  test('"stay signed in" makes the session cookie last about 30 days; otherwise it ends with the browser', async ({
    page,
  }) => {
    await signIn(page, accounts.alice);
    const plain = (await page.context().cookies()).find((c) => c.name === SESSION_COOKIE);
    await signOutViaApi(page);
    await page.context().clearCookies();

    await signIn(page, accounts.alice, { rememberMe: true });
    const remembered = (await page.context().cookies()).find((c) => c.name === SESSION_COOKIE);

    expect(plain?.expires).toBe(-1); // a session cookie
    const days = ((remembered?.expires ?? 0) - Date.now() / 1000) / 86_400;
    expect(days).toBeGreaterThan(29);
    expect(days).toBeLessThan(31);
  });

  test('after signing out the mailbox is closed again', async ({ page }) => {
    await signIn(page, accounts.bob);

    await signOutViaApi(page);
    await page.goto('/mail/inbox');
    await waitForAppReady(page);

    await expect(page).toHaveURL(/\/login$/);
  });

  test('a signed-in user is not kept on the login screen', async ({ page }) => {
    await signIn(page, accounts.alice);

    await page.goto('/login');
    await waitForAppReady(page);

    await expect(page).toHaveURL(/\/mail\/inbox$/);
  });

  test('too many wrong passwords lock the mailbox for a while', async ({ page }) => {
    test.setTimeout(180_000);
    const address = `nobody-${Date.now()}@vinette.test`; // an address of its own: no real mailbox gets locked
    await page.goto('/login');
    await waitForAppReady(page);

    for (let attempt = 0; attempt < 3; attempt++) {
      // The e2e backend locks after 3 failures (the default of 5 is covered by the integration tests).
      await submitLogin(page, address, 'wrong');
      await expect(page.getByRole('alert')).toContainText(
        'Неверный адрес или пароль',
        SLOW_REJECTION,
      );
    }
    await submitLogin(page, address, 'wrong');

    await expect(page.getByRole('alert')).toContainText('Слишком много попыток входа');
    await expect(page.getByRole('alert')).toContainText('мин');
  });
});
