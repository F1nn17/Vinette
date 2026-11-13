import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// The real-time channel, end to end: a mail that arrives while the mailbox is open shows up without anyone asking. The real backend and
// a real Dovecot (`make dev-mail`). The mail is put into the mailbox inside the mail server's container, the way the delivery of
// mail does (the path over SMTP is covered by the backend's integration tests). The mail is deleted again, so that the other
// scenarios find the mailbox as it was.

/** The container of the dev mail server: the one that publishes the IMAP port of `make dev-mail`. */
function mailContainer(): string {
  const named = process.env.VINETTE_E2E_MAIL_CONTAINER;
  if (named) return named;
  const found = execFileSync('docker', ['ps', '-q', '--filter', 'publish=3143'], {
    encoding: 'utf8',
  })
    .trim()
    .split(/\s+/)[0];
  if (!found) throw new Error('The dev mail server is not running (make dev-mail)');
  return found;
}

/** Puts a plain mail into a mailbox of the dev mail server (the same way the mail server stores a delivered one). */
function deliver(to: string, subject: string): Promise<void> {
  const message =
    `From: Sender <sender@vinette.test>
To: ${to}
Subject: ${subject}
` +
    `Message-ID: <${Date.now()}.${Math.random().toString(36).slice(2)}@vinette.test>
` +
    `Date: ${new Date().toUTCString()}

Hello.
`;
  execFileSync(
    'docker',
    ['exec', '-i', mailContainer(), 'doveadm', 'save', '-u', to, '-m', 'INBOX'],
    { input: message },
  );
  return Promise.resolve();
}

interface Item {
  id: string;
  subject: string;
}

async function inbox(page: Page): Promise<Item[]> {
  return page.evaluate(async () => {
    const response = await fetch('/api/v1/folders/INBOX/messages?limit=100&preview=false');
    return ((await response.json()) as { items: Item[] }).items;
  });
}

/** Removes the mail for good: to the trash, then out of it. */
async function remove(page: Page, subject: string): Promise<void> {
  await page.evaluate(async (wanted) => {
    const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
    const headers = { 'X-CSRF-Token': token, 'Content-Type': 'application/json' };
    const list = async (folder: string) =>
      (
        (await (
          await fetch(`/api/v1/folders/${folder}/messages?limit=100&preview=false`)
        ).json()) as { items: Item[] }
      ).items;
    const found = (await list('INBOX')).filter((i) => i.subject === wanted).map((i) => i.id);
    if (found.length > 0) {
      await fetch('/api/v1/messages/batch', {
        method: 'POST',
        headers,
        body: JSON.stringify({ ids: found, action: 'trash' }),
      });
    }
    const trashed = (await list('Trash')).filter((i) => i.subject === wanted).map((i) => i.id);
    if (trashed.length > 0) {
      await fetch('/api/v1/messages/batch', {
        method: 'POST',
        headers,
        body: JSON.stringify({ ids: trashed, action: 'delete' }),
      });
    }
  }, subject);
}

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
});
