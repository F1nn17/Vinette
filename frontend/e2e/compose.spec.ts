import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// Writing and sending, end to end: the real backend, a real Dovecot and Postfix (`make dev-mail`). Alice writes to herself, so that the
// scenario finds the message in her own "Sent" folder and inbox and removes both again (the other scenarios share the mailbox).

const SUBJECT = `Смета e2e ${Date.now()}`;

interface Folder {
  path: string;
  role: string;
}

interface Item {
  id: string;
  subject: string;
}

async function listFolder(page: Page, role: string): Promise<{ path: string; items: Item[] }> {
  return page.evaluate(async (wanted) => {
    const folders = ((await (await fetch('/api/v1/folders')).json()) as { folders: Folder[] })
      .folders;
    const folder = folders.find((f) => f.role === wanted);
    if (!folder) return { path: '', items: [] };
    const response = await fetch(
      `/api/v1/folders/${encodeURIComponent(folder.path)}/messages?limit=100&preview=false`,
    );
    return { path: folder.path, items: ((await response.json()) as { items: Item[] }).items };
  }, role);
}

/** Deletes the messages of the scenario for good (to the trash, then out of it) from both folders. */
async function cleanUp(page: Page): Promise<void> {
  for (const role of ['sent', 'inbox', 'drafts']) {
    const { items } = await listFolder(page, role);
    for (const item of items.filter((i) => i.subject === SUBJECT)) {
      await page.evaluate(async (id) => {
        const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
        const headers = { 'X-CSRF-Token': token };
        const moved = await fetch(`/api/v1/messages/${id}/trash`, { method: 'POST', headers });
        const { id: trashed } = (await moved.json()) as { id: string | null };
        if (trashed) await fetch(`/api/v1/messages/${trashed}`, { method: 'DELETE', headers });
      }, item.id);
    }
  }
}

test.describe('writing and sending', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.alice);
  });

  test.afterEach(async ({ page }) => {
    await cleanUp(page);
    await signOutViaApi(page);
  });

  test('write a message, send it, find it in "Sent" and in the inbox', async ({ page }) => {
    await page.getByRole('button', { name: 'Написать' }).first().click();
    await expect(page).toHaveURL(/\/compose/);

    await page.getByRole('combobox', { name: 'Кому', exact: true }).fill(accounts.alice.address);
    await page.keyboard.press('Enter');
    await expect(page.getByRole('list', { name: 'Получатели: Кому' })).toContainText(
      accounts.alice.address,
    );
    await page.getByLabel('Тема', { exact: true }).fill(SUBJECT);
    await page.getByRole('textbox', { name: 'Текст письма' }).click();
    await page.keyboard.type('Смотри вложение. Привет!');
    await expect(page.getByText(/Черновик сохранён в \d/)).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: 'Отправить', exact: true }).click();

    await expect(page).toHaveURL(/\/mail\/inbox/);
    await expect(page.getByText('Письмо отправлено')).toBeVisible();
    await expect
      .poll(async () => (await listFolder(page, 'sent')).items.some((i) => i.subject === SUBJECT), {
        timeout: 30_000,
      })
      .toBe(true);
    await expect
      .poll(
        async () => (await listFolder(page, 'inbox')).items.some((i) => i.subject === SUBJECT),
        {
          timeout: 60_000,
        },
      )
      .toBe(true);
    // the draft it was written from is gone
    expect((await listFolder(page, 'drafts')).items.some((i) => i.subject === SUBJECT)).toBe(false);
  });
});
