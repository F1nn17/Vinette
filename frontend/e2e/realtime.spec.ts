import { expect, test } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';
import { deliver, inbox, remove } from './mail-helpers';

// The real-time channel, end to end: a mail that arrives while the mailbox is open shows up without anyone asking. The mail is
// deleted again, so that the other scenarios find the mailbox as it was.

test.describe('real-time', () => {
  const subject = `Real-time ${Date.now()}`;

  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.alice);
  });

  test.afterEach(async ({ page }) => {
    await remove(page, subject);
    await signOutViaApi(page);
  });

  test('a mail that arrives while the inbox is open appears in the list, and the counter follows', async ({
    page,
  }) => {
    await page.goto('/mail/inbox');
    const sidebar = page.getByRole('navigation', { name: 'Папки' });
    await expect(sidebar.getByRole('link', { name: /Входящие/ })).toBeVisible();
    const unreadBefore = Number(
      (await sidebar.getByRole('link', { name: /Входящие/ }).innerText()).replace(/\D+/g, '') ||
        '0',
    );
    await page.waitForTimeout(2000); // the channel and the watch settle

    await deliver(accounts.alice.address, subject);

    await expect(page.getByRole('link', { name: new RegExp(subject) })).toBeVisible({
      timeout: 30_000,
    });
    await expect
      .poll(async () =>
        Number(
          (await sidebar.getByRole('link', { name: /Входящие/ }).innerText()).replace(/\D+/g, '') ||
            '0',
        ),
      )
      .toBe(unreadBefore + 1);
    // no reload happened: the page is the one that was opened
    expect((await inbox(page)).some((i) => i.subject === subject)).toBe(true);
  });

  test('a mail removed elsewhere leaves the list', async ({ page }) => {
    await deliver(accounts.alice.address, subject);
    await page.goto('/mail/inbox');
    await expect(page.getByRole('link', { name: new RegExp(subject) })).toBeVisible({
      timeout: 30_000,
    });
    await page.waitForTimeout(2000);

    await remove(page, subject); // another program (here: the API behind the screen's back) removes it

    await expect(page.getByRole('link', { name: new RegExp(subject) })).toHaveCount(0, {
      timeout: 30_000,
    });
  });

  test('our own removal is not echoed: the list is read once after it, not again when the server reports it', async ({
    page,
  }) => {
    await page.goto('/mail/inbox');
    const first = page.locator('a[href^="/mail/inbox/"]').first();
    await expect(first).toBeVisible();
    const subject = (await inbox(page))[0]?.subject ?? '';
    await page.waitForTimeout(2500); // the channel and the watch settle

    const shown = page.getByRole('link', { name: new RegExp(subject) });
    const before = await shown.count(); // other messages may carry the same words
    let listReads = 0;
    page.on('request', (request) => {
      if (/\/api\/v1\/folders\/INBOX\/messages/.test(request.url())) listReads++;
    });
    await page.getByRole('checkbox', { name: `Выбрать письмо «${subject}»` }).check();
    await page
      .getByRole('toolbar', { name: 'Действия с выбранными письмами' })
      .getByRole('button', { name: 'Удалить' })
      .click();
    const toast = page.getByRole('status').filter({ hasText: 'перенесено в корзину' });
    await expect(toast).toBeVisible();
    await page.waitForTimeout(5000); // the server reports the removal within a second or two

    expect(listReads).toBeLessThanOrEqual(1); // the one that follows our own action; the echo adds none
    await expect(shown).toHaveCount(before - 1);

    await toast.getByRole('button', { name: 'Отменить' }).click(); // put it back
    await expect
      .poll(async () => (await inbox(page)).some((i) => i.subject === subject))
      .toBe(true);
    // and it is in the list once, not twice
    await expect(shown).toHaveCount(before, { timeout: 30_000 });
  });
});
