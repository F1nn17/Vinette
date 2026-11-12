import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// Reading a message, end to end: the real backend, a real Dovecot and the test mails of `make seed`
// (backend/tests/fixtures; `make e2e` loads them). Bob's mailbox is used, so that the scenarios of sign-in (Alice) and
// reading do not delay each other.

/**
 * Opens a message by its subject through its address. The list is virtual (only the rows in view exist), so a message
 * further down is found in the answer of the server instead of by scrolling.
 */
async function openMessage(page: Page, subject: string): Promise<void> {
  const id = await page.evaluate(async (wanted) => {
    const response = await fetch('/api/v1/folders/INBOX/messages?limit=100&preview=false');
    const list = (await response.json()) as {
      items?: { id: string; subject: string }[];
      code?: string;
    };
    if (!list.items) throw new Error(`The list was refused: ${response.status} ${list.code ?? ''}`);
    return list.items.find((item) => item.subject === wanted)?.id;
  }, subject);
  expect(id, `the mailbox has a message "${subject}" (run make seed)`).toBeTruthy();
  await page.goto(`/mail/inbox/${id}`);
  await expect(page.getByRole('heading', { level: 1, name: subject })).toBeVisible();
}

test.describe('reading', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
  });

  // A session keeps its connection to the mail server until it ends, and a mailbox allows only a few: end it.
  test.afterEach(async ({ page }) => {
    await signOutViaApi(page);
  });

  test('opening a message shows its subject, sender and text, and the address holds the message', async ({
    page,
  }) => {
    await openMessage(page, 'Unicode attachment names');

    await expect(page).toHaveURL(/\/mail\/INBOX\/[^/]+$/i);
    await expect(page.getByText('Anna Smirnova').first()).toBeVisible();
    const frame = page.frameLocator('iframe[title="Unicode attachment names"]');
    await expect(frame.locator('body')).not.toBeEmpty();

    await page.reload();

    await expect(
      page.getByRole('heading', { level: 1, name: 'Unicode attachment names' }),
    ).toBeVisible();
  });

  test('attached files are listed and are downloaded, never opened in the page', async ({
    page,
  }) => {
    await openMessage(page, 'Unicode attachment names');

    const card = page.getByRole('link', { name: /Отчёт №1\.pdf/ });
    await expect(card).toBeVisible();
    const [download] = await Promise.all([page.waitForEvent('download'), card.click()]);

    expect(download.suggestedFilename()).toBe('Отчёт №1.pdf');
    await expect(
      page.getByRole('heading', { level: 1, name: 'Unicode attachment names' }),
    ).toBeVisible();
  });

  test('pictures from the web are hidden, and the browser never asks for them, even when they are shown', async ({
    page,
  }) => {
    const asked: string[] = [];
    page.on('request', (request) => asked.push(request.url()));

    await openMessage(page, 'Weekly newsletter');
    const notice = page.getByText(/Внешние изображения скрыты/);
    await expect(notice).toBeVisible();
    const frame = page.frameLocator('iframe[title="Weekly newsletter"]');
    await expect(frame.locator('body')).toContainText('Newsletter');
    expect(await frame.locator('img[src^="http"], img[src^="//"]').count()).toBe(0);

    await page.getByRole('button', { name: 'Показать', exact: true }).click();

    // The server fetches what it can (the addresses of the test mail lead nowhere, so nothing may come); either way the
    // notice gives way to the answer and the browser itself never talks to the sender's servers.
    await expect(notice).toBeHidden();
    expect(await frame.locator('img[src^="http"], img[src^="//"]').count()).toBe(0);
    expect(asked.filter((url) => /tracker\.example|cdn\.example\.org/.test(url))).toEqual([]);
  });

  test('a message that passes itself off as a bank carries a warning, and its page runs nothing', async ({
    page,
  }) => {
    const dialogs: string[] = [];
    page.on('dialog', (dialog) => {
      dialogs.push(dialog.message());
      void dialog.dismiss();
    });

    await openMessage(page, 'Your account is locked');

    await expect(
      page.getByRole('alert').filter({ hasText: 'Похоже на мошенничество' }),
    ).toBeVisible();
    expect(dialogs).toEqual([]);
  });

  test('a hostile message is shown without anything of it running', async ({ page }) => {
    const dialogs: string[] = [];
    page.on('dialog', (dialog) => {
      dialogs.push(dialog.message());
      void dialog.dismiss();
    });
    const asked: string[] = [];
    page.on('request', (request) => asked.push(request.url()));

    await openMessage(page, 'Urgent: verify your account');
    await page.waitForTimeout(1500);

    const frameElement = page.locator('iframe').first();
    await expect(frameElement).toBeVisible();
    expect(await frameElement.getAttribute('sandbox')).toBe(
      'allow-popups allow-popups-to-escape-sandbox',
    );
    expect(dialogs).toEqual([]);
    expect(asked.filter((url) => /evil|attacker/.test(url))).toEqual([]);
  });
});
