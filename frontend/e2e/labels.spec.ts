import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// Labels, end to end: the real backend, a real Dovecot and the test mails of `make seed` (see reading.spec.ts).
// Bob's mailbox. The scenario makes a label and removes it again, so that the others find the mailbox as it was.

const SUBJECT = 'Weekly newsletter';
// Not a simple word: the mail server keeps such a name in an encoded form, and the interface must show it as typed.
const LABEL = 'Отпуск 2026';

interface Item {
  id: string;
  subject: string;
  labels: string[];
}

async function inbox(page: Page): Promise<Item[]> {
  return page.evaluate(async () => {
    const response = await fetch('/api/v1/folders/INBOX/messages?limit=100&preview=false');
    return ((await response.json()) as { items: Item[] }).items;
  });
}

/** Deletes the label through the API (what a failed scenario leaves behind). */
async function dropLabel(page: Page, name: string): Promise<void> {
  await page.evaluate(async (label) => {
    const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
    await fetch(`/api/v1/labels/${encodeURIComponent(label)}`, {
      method: 'DELETE',
      headers: { 'X-CSRF-Token': token },
    });
  }, name);
}

test.describe('labels', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
  });

  test.afterEach(async ({ page }) => {
    await dropLabel(page, LABEL);
    await signOutViaApi(page);
  });

  test('make a label, put it on a message, find the message under the label, delete the label', async ({
    page,
  }) => {
    const sidebar = page.getByRole('navigation', { name: 'Папки' });
    const item = (await inbox(page)).find((i) => i.subject === SUBJECT);
    expect(item, `the mailbox has a message "${SUBJECT}" (run make seed)`).toBeTruthy();
    await page.goto(`/mail/inbox/${item?.id}`);
    await expect(page.getByRole('heading', { level: 1, name: SUBJECT })).toBeVisible();

    // a new label from the "+" of the sidebar
    await sidebar.getByRole('button', { name: 'Новая метка' }).click();
    await page.getByRole('textbox', { name: 'Название новой метки' }).fill(LABEL);
    await page.keyboard.press('Enter');
    await expect(sidebar.getByRole('link', { name: LABEL })).toBeVisible();

    // put it on the open message from the "Labels" panel
    await page
      .getByRole('toolbar', { name: 'Действия с письмом' })
      .getByRole('button', { name: 'Метки' })
      .click();
    // a click, not `check()`: the box follows the answer of the screen a moment after the click, and check() wants it at once
    await page.getByRole('checkbox', { name: LABEL }).click();
    await expect(page.getByRole('checkbox', { name: LABEL })).toBeChecked();
    await expect(
      page.getByRole('article').locator('[data-color]', { hasText: LABEL }),
    ).toBeVisible();
    await expect
      .poll(async () => (await inbox(page)).find((i) => i.id === item?.id)?.labels)
      .toContain(LABEL);
    await page.keyboard.press('Escape');

    // the label's own list shows the message
    await sidebar.getByRole('link', { name: LABEL }).click();
    await expect(page).toHaveURL(/\/mail\/@label\//);
    await expect(page.getByRole('heading', { level: 1, name: LABEL })).toBeVisible();
    await expect(page.getByRole('link', { name: new RegExp(SUBJECT) })).toBeVisible();

    // delete it: asked first, then gone from the sidebar and from the message
    await sidebar.getByRole('button', { name: `Действия с меткой «${LABEL}»` }).click();
    await page.getByRole('button', { name: 'Удалить', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Удалить метку' }).click();
    await expect(sidebar.getByRole('link', { name: LABEL })).toHaveCount(0);
    await expect(page).toHaveURL(/\/mail\/inbox/);
    await expect
      .poll(async () => (await inbox(page)).find((i) => i.id === item?.id)?.labels)
      .not.toContain(LABEL);
  });
});
