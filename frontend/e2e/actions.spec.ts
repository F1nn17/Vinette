import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// Actions on messages, end to end: the real backend, a real Dovecot and the test mails of `make seed` (see reading.spec.ts).
// Bob's mailbox again. Every scenario puts the mailbox back as it found it, so that the others can rely on it.

interface Item {
  id: string;
  subject: string;
  seen: boolean;
  flagged: boolean;
}

/** The messages of a folder as the server lists them. */
async function listOf(page: Page, folder: string): Promise<Item[]> {
  return page.evaluate(async (f) => {
    const response = await fetch(
      `/api/v1/folders/${encodeURIComponent(f)}/messages?limit=100&preview=false`,
    );
    return ((await response.json()) as { items: Item[] }).items;
  }, folder);
}

async function subjectsOf(page: Page, folder: string): Promise<string[]> {
  return (await listOf(page, folder)).map((i) => i.subject);
}

/** Opens a message by its subject through its address (the list is virtual, see reading.spec.ts). */
async function openMessage(page: Page, subject: string): Promise<void> {
  const item = (await listOf(page, 'INBOX')).find((i) => i.subject === subject);
  expect(item, `the mailbox has a message "${subject}" (run make seed)`).toBeTruthy();
  await page.goto(`/mail/inbox/${item?.id}`);
  await expect(page.getByRole('heading', { level: 1, name: subject })).toBeVisible();
}

const SUBJECT = 'Weekly newsletter';

test.describe('actions', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
  });

  // A session keeps its connection to the mail server until it ends, and a mailbox allows only a few: end it.
  test.afterEach(async ({ page }) => {
    // Whatever a failed scenario left in the trash goes back to the inbox.
    const left = (await listOf(page, 'Trash')).map((i) => i.id);
    if (left.length > 0) {
      await page.evaluate(async (ids) => {
        const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
        await fetch('/api/v1/messages/batch', {
          method: 'POST',
          headers: { 'X-CSRF-Token': token, 'Content-Type': 'application/json' },
          body: JSON.stringify({ ids, action: 'move', to: 'INBOX' }),
        });
      }, left);
    }
    await signOutViaApi(page);
  });

  test('delete, then undo: the message is back in the inbox', async ({ page }) => {
    await openMessage(page, SUBJECT);
    const before = await subjectsOf(page, 'INBOX');

    await page
      .getByRole('toolbar', { name: 'Действия с письмом' })
      .getByRole('button', { name: 'Удалить' })
      .click();

    const toast = page.getByRole('status').filter({ hasText: 'перенесено в корзину' });
    await expect(toast).toBeVisible();
    await expect(page).toHaveURL(/\/mail\/inbox$/);
    await expect.poll(() => subjectsOf(page, 'INBOX')).not.toContain(SUBJECT);
    expect(await subjectsOf(page, 'Trash')).toContain(SUBJECT);

    await toast.getByRole('button', { name: 'Отменить' }).click();

    await expect.poll(() => subjectsOf(page, 'INBOX')).toContain(SUBJECT);
    expect(await subjectsOf(page, 'Trash')).not.toContain(SUBJECT);
    expect((await subjectsOf(page, 'INBOX')).length).toBe(before.length);
    // and the list on the screen shows it again
    await page.reload();
    await expect(page.getByRole('link', { name: new RegExp(SUBJECT) })).toBeVisible();
  });

  test('delete without undo: the message stays in the trash and can be found there', async ({
    page,
  }) => {
    await openMessage(page, SUBJECT);

    await page
      .getByRole('toolbar', { name: 'Действия с письмом' })
      .getByRole('button', { name: 'Удалить' })
      .click();

    await expect(
      page.getByRole('status').filter({ hasText: 'перенесено в корзину' }),
    ).toBeVisible();
    await expect.poll(() => subjectsOf(page, 'Trash')).toContain(SUBJECT);
    await page.getByRole('link', { name: /Корзина/ }).click();
    await expect(page.getByRole('link', { name: new RegExp(SUBJECT) })).toBeVisible();
  });

  test('several selected messages go to the trash together and come back with one undo', async ({
    page,
  }) => {
    const items = (await listOf(page, 'INBOX')).slice(0, 3);
    await page.goto('/mail/inbox');
    for (const item of items) {
      await page.getByRole('checkbox', { name: `Выбрать письмо «${item.subject}»` }).check();
    }
    await expect(page.getByText('Выбрано: 3')).toBeVisible();

    await page
      .getByRole('toolbar', { name: 'Действия с выбранными письмами' })
      .getByRole('button', { name: 'Удалить' })
      .click();

    const toast = page.getByRole('status').filter({ hasText: 'перенесены в корзину' });
    await expect(toast).toBeVisible();
    await expect.poll(async () => (await listOf(page, 'Trash')).length).toBe(3);

    await toast.getByRole('button', { name: 'Отменить' }).click();

    await expect.poll(async () => (await listOf(page, 'Trash')).length).toBe(0);
    expect((await subjectsOf(page, 'INBOX')).sort()).toEqual(
      expect.arrayContaining(items.map((i) => i.subject)),
    );
  });

  test('the keyboard: j and Enter open a message, e# need something to act on, ? lists the keys', async ({
    page,
  }) => {
    await page.goto('/mail/inbox');
    await expect(page.getByRole('checkbox').first()).toBeAttached();
    await expect(page.getByRole('dialog')).toHaveCount(0); // no hints on the screen

    await page.keyboard.press('j');
    await expect(page.locator('a[data-cursor="true"]')).toHaveCount(1);
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/mail\/inbox\/[^/]+$/);
    await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();

    await page.keyboard.press('Shift+/');
    const dialog = page.getByRole('dialog', { name: 'Горячие клавиши' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText('Следующее письмо')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);

    await page.keyboard.press('/');
    await expect(page.getByRole('searchbox', { name: 'Поиск в почте' })).toBeFocused();
    await page.keyboard.type('jjj#'); // typing in the search field is only typing
    await expect(page).toHaveURL(/\/mail\/inbox\/[^/]+$/);
  });

  test('# deletes the message under the cursor and the undo brings it back', async ({ page }) => {
    await page.goto('/mail/inbox');
    const first = (await listOf(page, 'INBOX'))[0];
    expect(first).toBeTruthy();
    await expect(page.getByRole('link', { name: new RegExp(first?.subject ?? '') })).toBeVisible();

    await page.keyboard.press('j');
    await page.keyboard.press('Shift+3');

    const toast = page.getByRole('status').filter({ hasText: 'перенесено в корзину' });
    await expect(toast).toBeVisible();
    await expect.poll(() => subjectsOf(page, 'Trash')).toContain(first?.subject);
    await toast.getByRole('button', { name: 'Отменить' }).click();
    await expect.poll(() => subjectsOf(page, 'INBOX')).toContain(first?.subject);
  });

  test('a refused action puts the message back and says so', async ({ page }) => {
    // The test mailbox has no archive folder, so the server refuses: the row must come back with an explanation.
    await page.goto('/mail/inbox');
    const subject = (await listOf(page, 'INBOX'))[0]?.subject ?? '';
    await page.getByRole('checkbox', { name: `Выбрать письмо «${subject}»` }).check();

    await page
      .getByRole('toolbar', { name: 'Действия с выбранными письмами' })
      .getByRole('button', { name: 'В архив' })
      .click();

    await expect(page.getByRole('alert').filter({ hasText: 'нет подходящей папки' })).toBeVisible();
    await expect(page.getByRole('link', { name: new RegExp(subject) })).toBeVisible();
    expect(await subjectsOf(page, 'INBOX')).toContain(subject);
  });

  test('spam, then undo: the message goes to the spam folder and comes back', async ({ page }) => {
    await openMessage(page, SUBJECT);

    await page
      .getByRole('toolbar', { name: 'Действия с письмом' })
      .getByRole('button', { name: 'В спам' })
      .click();

    const toast = page.getByRole('status').filter({ hasText: 'перенесено в спам' });
    await expect(toast).toBeVisible();
    await expect.poll(() => subjectsOf(page, 'Junk')).toContain(SUBJECT);
    expect(await subjectsOf(page, 'INBOX')).not.toContain(SUBJECT);

    await toast.getByRole('button', { name: 'Отменить' }).click();

    await expect.poll(() => subjectsOf(page, 'INBOX')).toContain(SUBJECT);
    expect(await subjectsOf(page, 'Junk')).not.toContain(SUBJECT);
  });

  test('the flag is set and removed from the open message and the server keeps it', async ({
    page,
  }) => {
    await openMessage(page, SUBJECT);
    const bar = page.getByRole('toolbar', { name: 'Действия с письмом' });
    const flagged = async () =>
      (await listOf(page, 'INBOX')).find((i) => i.subject === SUBJECT)?.flagged;
    expect(await flagged()).toBe(false);

    await bar.getByRole('button', { name: 'Флажок' }).click();
    await expect(bar.getByRole('button', { name: 'Флажок' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect.poll(flagged).toBe(true);

    await bar.getByRole('button', { name: 'Флажок' }).click();
    await expect(bar.getByRole('button', { name: 'Флажок' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
    await expect.poll(flagged).toBe(false);
  });

  test('"Mark as unread" from "More" leaves the message unread, and opening it reads it again', async ({
    page,
  }) => {
    await openMessage(page, SUBJECT);
    const seen = async () => (await listOf(page, 'INBOX')).find((i) => i.subject === SUBJECT)?.seen;
    await expect.poll(seen).toBe(true); // opening read it

    await page
      .getByRole('toolbar', { name: 'Действия с письмом' })
      .getByRole('button', { name: 'Ещё' })
      .click();
    await page.getByRole('button', { name: 'Отметить непрочитанным' }).click();

    await expect(page).toHaveURL(/\/mail\/inbox$/);
    await expect.poll(seen).toBe(false);

    await openMessage(page, SUBJECT);
    await expect.poll(seen).toBe(true);
  });

  test('selected unread messages are marked as read at once', async ({ page }) => {
    const items = (await listOf(page, 'INBOX')).slice(0, 2);
    await page.evaluate(
      async (ids) => {
        const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
        await fetch('/api/v1/messages/batch', {
          method: 'POST',
          headers: { 'X-CSRF-Token': token, 'Content-Type': 'application/json' },
          body: JSON.stringify({ ids, action: 'unread' }),
        });
      },
      items.map((i) => i.id),
    );
    await expect
      .poll(async () => (await listOf(page, 'INBOX')).slice(0, 2).some((i) => !i.seen))
      .toBe(true);
    await page.goto('/mail/inbox');
    for (const item of items) {
      await page.getByRole('checkbox', { name: `Выбрать письмо «${item.subject}»` }).check();
    }

    await page
      .getByRole('toolbar', { name: 'Действия с выбранными письмами' })
      .getByRole('button', { name: 'Отметить прочитанными' })
      .click();

    await expect
      .poll(async () => (await listOf(page, 'INBOX')).slice(0, 2).every((i) => i.seen))
      .toBe(true);
    await expect(page.getByText(/Выбрано:/u)).toHaveCount(0); // the selection ended with the action
  });
});
