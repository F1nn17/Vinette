import { expect, test, type Page } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';

// Search in the list, end to end: the real backend and a real Dovecot with the test mails of `make seed` (backend/tests/fixtures).
// Bob's mailbox is used. Cyrillic words are searched for in mail that came in different charsets.

const searchBox = (page: Page) => page.getByRole('searchbox', { name: 'Поиск в почте' });
/** The rows of the list (the links of the sidebar are not among them). */
const rows = (page: Page) => page.locator('a[href^="/mail/inbox/"]');

test.describe('search', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.bob);
  });

  test.afterEach(async ({ page }) => {
    await signOutViaApi(page);
  });

  test('a Cyrillic word finds the message, the address keeps the search, Escape brings the folder back', async ({
    page,
  }) => {
    await searchBox(page).fill('квартал');

    await expect(page.getByRole('link', { name: /Отчёт за квартал/ })).toBeVisible();
    await expect(rows(page)).toHaveCount(1);
    await expect(page.getByRole('heading', { level: 1 })).toContainText('найдено 1');
    await expect(page).toHaveURL(/[?&]q=/);

    // a reload keeps the search and the box
    await page.reload();
    await expect(searchBox(page)).toHaveValue('квартал');
    await expect(page.getByRole('link', { name: /Отчёт за квартал/ })).toBeVisible();

    // the open message keeps the search in the address, so Back to the list is still the search
    await page.getByRole('link', { name: /Отчёт за квартал/ }).click();
    await expect(page).toHaveURL(/\/mail\/inbox\/[^?]+\?.*q=/);
    await expect(page.getByRole('heading', { level: 1, name: 'Отчёт за квартал' })).toBeVisible();

    await searchBox(page).press('Escape');
    await expect(searchBox(page)).toHaveValue('');
    await expect(page).not.toHaveURL(/[?&]q=/);
    await expect(rows(page).nth(5)).toBeVisible();
  });

  test('a word that is in the text of mail in KOI8-R and Windows-1251 finds both', async ({
    page,
  }) => {
    await searchBox(page).fill('кодировке');
    await searchBox(page).press('Enter');

    await expect(page.getByRole('link', { name: /Тест KOI8-R/ })).toBeVisible();
    await expect(page.getByRole('link', { name: /Отчёт за квартал/ })).toBeVisible();
  });

  test('a search that finds nothing says so and "Clear search" brings the folder back', async ({
    page,
  }) => {
    await searchBox(page).fill('нет-такого-слова-в-почте');

    await expect(page.getByText(/Ничего не найдено по запросу/)).toBeVisible();
    await page.getByRole('button', { name: 'Сбросить поиск' }).click();

    await expect(page.getByText(/Ничего не найдено/)).toHaveCount(0);
    await expect(rows(page).first()).toBeVisible();
    await expect(searchBox(page)).toHaveValue('');
  });

  test('the key "/" puts the cursor in the search box', async ({ page }) => {
    await page.locator('body').click();
    await page.keyboard.press('/');

    await expect(searchBox(page)).toBeFocused();
  });
});
