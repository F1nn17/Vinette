import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';
import { deleteBySubject, emptyScheduled, listFolder } from './compose-helpers';

// Writing, the other ways in: reply, forward, a draft that is gone on with, a message put off. The real backend and a real Dovecot and
// Postfix (`make dev-mail`). Replies and forwards are only opened (an answer would go to an address of the fixture); what the other
// scenarios write is addressed to a mailbox of the dev server and is removed again.

const SUBJECT = `Черновик e2e ${Date.now()}`;
const LATER = `Позже e2e ${Date.now()}`;

/** Opens a message of the inbox by its subject (the list is virtual, so it is found through the API). */
async function openInbox(page: Page, subject: string): Promise<void> {
  const item = (await listFolder(page, 'inbox')).find((i) => i.subject === subject);
  expect(item, `the mailbox has a message "${subject}" (run make seed)`).toBeTruthy();
  await page.goto(`/mail/inbox/${item!.id}`);
  await expect(page.getByRole('heading', { level: 1, name: subject })).toBeVisible();
}

async function addRecipient(page: Page, address: string): Promise<void> {
  await page.getByRole('combobox', { name: 'Кому', exact: true }).fill(address);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('list', { name: 'Получатели: Кому' })).toContainText(address);
}

test.describe('writing: reply and forward', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
  });
  test.afterEach(async ({ page }) => {
    await signOutViaApi(page);
  });

  test('"Reply" opens the editor with the sender, "Re:" and the quoted message', async ({
    page,
  }) => {
    await openInbox(page, 'Re: Привет, мир');
    await page.getByRole('button', { name: 'Ответить', exact: true }).click();

    await expect(page).toHaveURL(/\/compose/);
    await expect(page.getByLabel('Тема', { exact: true })).toHaveValue('Re: Привет, мир');
    await expect(page.getByRole('list', { name: 'Получатели: Кому' })).not.toBeEmpty();
    await expect(page.getByRole('textbox', { name: 'Текст письма' })).not.toBeEmpty();
  });

  test('"Forward" opens the editor with "Fwd:", no recipient and the message inside', async ({
    page,
  }) => {
    await openInbox(page, 'Re: Привет, мир');
    await page.getByRole('button', { name: 'Переслать', exact: true }).click();

    await expect(page).toHaveURL(/\/compose/);
    await expect(page.getByLabel('Тема', { exact: true })).toHaveValue('Fwd: Привет, мир');
    await expect(page.getByRole('textbox', { name: 'Текст письма' })).toContainText(
      'Пересылаемое письмо',
    );
  });
});

test.describe('writing: drafts and sending later', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.alice);
  });
  test.afterEach(async ({ page }) => {
    await emptyScheduled(page);
    await deleteBySubject(page, SUBJECT, ['drafts']);
    await deleteBySubject(page, LATER, ['drafts', 'sent', 'inbox']);
    await signOutViaApi(page);
  });

  test('a draft is saved, found in "Drafts", gone on with, and deleted', async ({ page }) => {
    await page.getByRole('button', { name: 'Написать' }).first().click();
    await addRecipient(page, accounts.bob.address);
    await page.getByLabel('Тема', { exact: true }).fill(SUBJECT);
    await page.getByRole('textbox', { name: 'Текст письма' }).click();
    await page.keyboard.type('Дописать позже');
    await expect(page.getByText(/Черновик сохранён в \d/)).toBeVisible({ timeout: 15_000 });

    await expect
      .poll(async () => (await listFolder(page, 'drafts')).some((i) => i.subject === SUBJECT), {
        timeout: 30_000,
      })
      .toBe(true);

    const draft = (await listFolder(page, 'drafts')).find((i) => i.subject === SUBJECT)!;
    await page.goto(`/mail/drafts/${draft.id}`);
    await page.getByRole('button', { name: 'Редактировать', exact: true }).click();

    await expect(page).toHaveURL(/\/compose/);
    await expect(page.getByLabel('Тема', { exact: true })).toHaveValue(SUBJECT);
    await expect(page.getByRole('list', { name: 'Получатели: Кому' })).toContainText(
      accounts.bob.address,
    );
    await expect(page.getByRole('textbox', { name: 'Текст письма' })).toContainText(
      'Дописать позже',
    );

    await page.getByRole('button', { name: 'Удалить черновик', exact: true }).click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Удалить черновик', exact: true })
      .click();
    await expect(page.getByText('Черновик удалён')).toBeVisible();
    await expect
      .poll(async () => (await listFolder(page, 'drafts')).some((i) => i.subject === SUBJECT), {
        timeout: 30_000,
      })
      .toBe(false);
  });

  test('a message put off waits in "Scheduled" and goes back to "Drafts" when cancelled', async ({
    page,
  }) => {
    await page.getByRole('button', { name: 'Написать' }).first().click();
    await addRecipient(page, accounts.alice.address);
    await page.getByLabel('Тема', { exact: true }).fill(LATER);
    await page.getByRole('textbox', { name: 'Текст письма' }).click();
    await page.keyboard.type('Уйдёт завтра');

    await page.getByRole('button', { name: 'Отправить позже', exact: true }).click();
    await page.getByRole('button', { name: 'Завтра в 8:00' }).click();
    await page.getByRole('button', { name: 'Запланировать' }).click();

    await expect(page).toHaveURL(/\/mail\/inbox/);
    await expect(page.getByText(/Письмо уйдёт:/)).toBeVisible();

    await page.getByRole('link', { name: /Отложенные/ }).click();
    await expect(page).toHaveURL(/\/mail\/@scheduled/);
    await page.getByRole('link', { name: new RegExp(LATER) }).click();
    await expect(page.getByRole('heading', { level: 1, name: LATER })).toBeVisible();

    await page.getByRole('button', { name: 'Отменить отправку' }).click();

    await expect(page.getByText('Письмо возвращено в «Черновики»')).toBeVisible();
    await expect(page.getByText(/Отложенных писем нет/)).toBeVisible();
    await expect
      .poll(async () => (await listFolder(page, 'drafts')).some((i) => i.subject === LATER), {
        timeout: 30_000,
      })
      .toBe(true);
  });
});
