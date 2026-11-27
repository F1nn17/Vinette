import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// The application under its own Content-Security-Policy, in a real browser: every screen that matters is visited and the browser must not
// have blocked anything. (A policy that is too tight breaks the page silently; this is what finds it.) Needs the page to come from the
// backend that sets the headers: the container, or the backend serving the built application; the Vite dev server does not send them.

/** Collects what the browser reports as blocked by the policy. */
async function watchViolations(page: Page): Promise<string[]> {
  const violations: string[] = [];
  page.on('console', (message) => {
    if (/Content Security Policy|Refused to/i.test(message.text())) violations.push(message.text());
  });
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (event) => {
      console.error(`Refused to: ${event.violatedDirective} ${event.blockedURI}`);
    });
  });
  return violations;
}

test.describe('content security policy', () => {
  test('the pages carry the policy and the other security headers', async ({ page }) => {
    const response = await page.goto('/login');
    const headers = response!.headers();

    expect(headers['content-security-policy']).toContain("script-src 'self'");
    expect(headers['content-security-policy']).not.toMatch(/script-src[^;]*unsafe/);
    expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['x-frame-options']).toBe('DENY');
    expect(headers['referrer-policy']).toBe('no-referrer');
    expect(headers['permissions-policy']).toContain('camera=()');
  });

  test('the browser refuses a script that is put into the page', async ({ page }) => {
    const violations = await watchViolations(page);
    await page.goto('/login');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

    const ran = await page.evaluate(() => {
      const script = document.createElement('script');
      script.textContent = 'window.__injected = true';
      document.head.appendChild(script);
      return (window as unknown as { __injected?: boolean }).__injected === true;
    });

    expect(ran).toBe(false);
    await expect.poll(() => violations.length).toBeGreaterThan(0);
  });

  test('the screens work under the policy: nothing is blocked', async ({ page }) => {
    const violations = await watchViolations(page);
    await signIn(page, accounts.bob);

    // the list, a message in its frame, the hostile one (the list is virtual, so a message is found through the API)
    await expect(page.locator('a[href^="/mail/inbox/"]').first()).toBeVisible();
    for (const subject of ['Unicode attachment names', 'Weekly newsletter']) {
      const id = await page.evaluate(async (wanted) => {
        const response = await fetch('/api/v1/folders/INBOX/messages?limit=100&preview=false');
        const list = (await response.json()) as { items: { id: string; subject: string }[] };
        return list.items.find((item) => item.subject === wanted)?.id;
      }, subject);
      expect(id, `the mailbox has "${subject}" (run make seed)`).toBeTruthy();
      await page.goto(`/mail/inbox/${id}`);
      await expect(page.getByRole('heading', { level: 1, name: subject })).toBeVisible();
      await expect(page.locator(`iframe[title="${subject}"]`)).toBeVisible();
    }

    // search, the editor, the settings, the queue of scheduled messages
    await page.goto('/mail/inbox');
    await page.getByRole('searchbox', { name: 'Поиск в почте' }).fill('квартал');
    await expect(page.locator('a[href^="/mail/inbox/"]')).toHaveCount(1);
    await page.getByRole('button', { name: 'Написать' }).first().click();
    await expect(page.getByRole('textbox', { name: 'Текст письма' })).toBeVisible();
    await page.goto('/settings');
    await expect(page.getByRole('heading', { level: 1, name: 'Настройки' })).toBeVisible();
    await page.goto('/mail/@scheduled');
    await expect(page.getByRole('heading', { level: 1, name: 'Отложенные' })).toBeVisible();

    // the live channel is open and the fonts are loaded
    await expect
      .poll(() => page.evaluate(() => document.fonts.check('16px "Golos Text"')))
      .toBe(true);

    expect(violations).toEqual([]);
    await signOutViaApi(page);
  });
});
