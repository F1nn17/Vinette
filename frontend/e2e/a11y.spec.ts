import { expect, test, type Page } from '@playwright/test';
import { expectAccessible, expectMessageFrameTitled } from './a11y-helpers';
import { accounts, signIn, signOutViaApi, submitLogin, waitForAppReady } from './helpers';

// The automatic part of the accessibility audit (VIN-130, docs/accessibility.md): every key screen, in the states a person meets, goes through
// axe-core with the rules of WCAG 2.2 A and AA. The rest of the audit (keyboard, screen reader, zoom, reduced motion) is in the same document and
// in `keyboard` below. Bob's mailbox is used (his mails are the fixtures of `make seed`).

async function openMessage(page: Page, subject: string): Promise<void> {
  const id = await page.evaluate(async (wanted) => {
    const response = await fetch('/api/v1/folders/INBOX/messages?limit=100&preview=false');
    const list = (await response.json()) as { items?: { id: string; subject: string }[] };
    return list.items?.find((item) => item.subject === wanted)?.id;
  }, subject);
  expect(id, `the mailbox has a message "${subject}" (run make seed)`).toBeTruthy();
  await page.goto(`/mail/inbox/${id}`);
  await expect(page.getByRole('heading', { level: 1, name: subject })).toBeVisible();
}

test.describe('accessibility: sign-in', () => {
  test('the sign-in screen', async ({ page }) => {
    await page.goto('/login');
    await waitForAppReady(page);
    await expect(page.getByRole('button', { name: 'Войти', exact: true })).toBeVisible();
    await expectAccessible(page, 'sign-in');
  });

  test('the sign-in screen with an error', async ({ page }) => {
    await page.goto('/login');
    await waitForAppReady(page);
    await submitLogin(page, 'nobody@vinette.test', 'wrong-password');
    await expect(page.getByRole('alert')).toBeVisible({ timeout: 40_000 });
    await expectAccessible(page, 'sign-in with an error');
  });

  test('the sign-in screen on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 });
    await page.goto('/login');
    await waitForAppReady(page);
    await expect(page.getByRole('button', { name: 'Войти', exact: true })).toBeVisible();
    await expectAccessible(page, 'sign-in on a phone');
  });
});

test.describe('accessibility: the mailbox', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
    await expect(page.getByRole('checkbox').first()).toBeVisible();
  });

  test.afterEach(async ({ page }) => {
    await signOutViaApi(page);
  });

  test('the list of messages', async ({ page }) => {
    await expectAccessible(page, 'the list');
  });

  test('an open message', async ({ page }) => {
    await openMessage(page, 'Unicode attachment names');
    await expectMessageFrameTitled(page, 'Unicode attachment names');
    await expectAccessible(page, 'an open message');
  });

  test('the account menu', async ({ page }) => {
    await page.getByRole('button', { name: /Меню аккаунта/ }).click();
    await expect(page.getByRole('menuitem', { name: 'Настройки' })).toBeVisible();
    await expectAccessible(page, 'the account menu');
  });

  test('the window of keyboard shortcuts', async ({ page }) => {
    await page.keyboard.press('?');
    await expect(page.getByRole('dialog')).toBeVisible();
    await expectAccessible(page, 'the shortcuts window');
  });

  test('the list on a phone, and an open message on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 });
    await page.goto('/mail/inbox');
    await expect(
      page.getByRole('region', { name: 'Список писем' }).getByRole('link').first(),
    ).toBeVisible();
    await expectAccessible(page, 'the list on a phone');
    await openMessage(page, 'Unicode attachment names');
    await expectMessageFrameTitled(page, 'Unicode attachment names');
    await expectAccessible(page, 'an open message on a phone');
  });
});

test.describe('accessibility: writing, scheduled mail and settings', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
  });

  test.afterEach(async ({ page }) => {
    // Bob's language goes back to "as the browser's": the other scenarios read Russian.
    await page.evaluate(async () => {
      const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
      await fetch('/api/v1/settings', {
        method: 'PATCH',
        headers: { 'X-CSRF-Token': token, 'Content-Type': 'application/json' },
        body: JSON.stringify({ language: '' }),
      });
    });
    await signOutViaApi(page);
  });

  test('the new message screen', async ({ page }) => {
    await page.goto('/compose');
    await expect(page.getByRole('button', { name: 'Отправить', exact: true })).toBeVisible();
    await expectAccessible(page, 'the new message');
  });

  test('the scheduled messages', async ({ page }) => {
    await page.goto('/mail/@scheduled');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expectAccessible(page, 'scheduled');
  });

  test('the settings', async ({ page }) => {
    await page.goto('/settings');
    await expect(page.getByRole('heading', { level: 1, name: 'Настройки' })).toBeVisible();
    await expectAccessible(page, 'the settings');
  });

  test('the settings in English', async ({ page }) => {
    await page.goto('/settings');
    await expect(page.getByRole('heading', { level: 1, name: 'Настройки' })).toBeVisible();
    await page.getByRole('combobox', { name: 'Язык' }).selectOption('en');
    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await expectAccessible(page, 'the settings in English');
  });
});

test.describe('accessibility: zoom, motion and the keyboard', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
    await expect(page.getByRole('checkbox').first()).toBeVisible();
  });

  test.afterEach(async ({ page }) => {
    await signOutViaApi(page);
  });

  // WCAG 1.4.10 (reflow): at 320 CSS pixels wide (a 400 % zoom of a 1280 px window) nothing needs a sideways scroll of the page.
  for (const [name, path] of [
    ['the list', '/mail/inbox'],
    ['the new message', '/compose'],
    ['the settings', '/settings'],
  ] as const) {
    test(`${name} does not scroll sideways at 320 pixels`, async ({ page }) => {
      await page.setViewportSize({ width: 320, height: 568 });
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow, `${name}: how many pixels wider than the window`).toBeLessThanOrEqual(0);
    });
  }

  // WCAG 2.3.3 / the operating system's "reduce motion": no animation or transition lasts longer than a blink.
  test('with reduced motion nothing animates', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.getByRole('button', { name: /Меню аккаунта/ }).click();
    await expect(page.getByRole('menuitem', { name: 'Настройки' })).toBeVisible();
    const longest = await page.evaluate(() => {
      const seconds = (value: string) =>
        Math.max(
          0,
          ...value
            .split(',')
            .map((part) => Number.parseFloat(part) * (part.includes('ms') ? 0.001 : 1)),
        );
      return Math.max(
        0,
        ...[...document.querySelectorAll('*')].map((element) => {
          const style = getComputedStyle(element);
          return Math.max(seconds(style.animationDuration), seconds(style.transitionDuration));
        }),
      );
    });
    expect(longest, 'the longest animation or transition, in seconds').toBeLessThanOrEqual(0.001);
  });

  // WCAG 2.1.1, 2.1.2, 2.4.3, 2.4.7: everything is reachable with the keyboard, focus is visible, focus is never trapped and goes back.
  test('the account menu opens and closes with the keyboard and focus goes back to its button', async ({
    page,
  }) => {
    const button = page.getByRole('button', { name: /Меню аккаунта/ });
    await button.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('menuitem', { name: 'Настройки' })).toBeVisible();
    await expect(page.getByRole('menuitem').first()).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('menuitem').nth(1)).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('menuitem', { name: 'Настройки' })).toBeHidden();
    await expect(button).toBeFocused();
  });

  test('the shortcuts window takes the focus, keeps it while Tab goes round, and gives it back on Escape', async ({
    page,
  }) => {
    await page
      .getByRole('searchbox')
      .or(page.getByRole('button', { name: 'Поиск в почте' }))
      .first()
      .focus();
    const before = await page.evaluate(() => document.activeElement?.outerHTML.slice(0, 80));
    await page.keyboard.press('Escape');
    await page.locator('body').click({ position: { x: 1, y: 1 } });
    await page.keyboard.press('?');
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    for (let i = 0; i < 6; i += 1) {
      await page.keyboard.press('Tab');
      expect(
        await dialog.evaluate((card) => card.contains(document.activeElement)),
        `Tab ${i + 1}`,
      ).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    expect(before, 'a starting point existed').toBeTruthy();
  });

  test('every control that takes the focus shows a focus ring', async ({ page }) => {
    const missing: string[] = [];
    for (let i = 0; i < 25; i += 1) {
      await page.keyboard.press('Tab');
      const state = await page.evaluate(() => {
        const element = document.activeElement;
        if (!element || element === document.body) return null;
        const style = getComputedStyle(element);
        const visible = style.outlineStyle !== 'none' && Number.parseFloat(style.outlineWidth) >= 2;
        return {
          visible,
          label:
            element.getAttribute('aria-label') ??
            element.textContent?.trim().slice(0, 30) ??
            element.tagName,
        };
      });
      if (state && !state.visible) missing.push(state.label);
    }
    expect(missing, 'elements that took the focus with no ring').toEqual([]);
  });
});

test.describe('accessibility: the title of the tab names the screen', () => {
  test('the sign-in screen, the folder, an open message, the settings and the new message each have their own title', async ({
    page,
  }) => {
    await page.goto('/login');
    await expect(page).toHaveTitle('Вход — Vinette');

    await signIn(page, accounts.bob);
    await expect(page).toHaveTitle('Входящие — Vinette');

    await openMessage(page, 'Unicode attachment names');
    await expect(page).toHaveTitle('Unicode attachment names — Vinette');

    await page.goto('/settings');
    await expect(page).toHaveTitle('Настройки — Vinette');

    await page.goto('/compose');
    await expect(page).toHaveTitle(/ — Vinette$/);
    await expect(page).not.toHaveTitle('Vinette');
    await signOutViaApi(page);
  });
});
