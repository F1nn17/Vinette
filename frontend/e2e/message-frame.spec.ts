import { expect, test } from '@playwright/test';

// The catalogue page (/dev/ui, dev builds only) shows a hostile mail in the real frame. A script, an event handler and a
// javascript: link in it must do nothing in a real browser; unit tests can only check the attributes.
test('the frame runs nothing of a hostile mail', async ({ page }) => {
  const requests: string[] = [];
  page.on('request', (request) => requests.push(request.url()));
  const dialogs: string[] = [];
  page.on('dialog', (dialog) => {
    dialogs.push(dialog.message());
    void dialog.dismiss();
  });

  await page.goto('/dev/ui');
  const frameElement = page.getByTitle('Hostile sample');
  await expect(frameElement).toBeVisible();
  await page.waitForTimeout(1500);

  const sandbox = await frameElement.getAttribute('sandbox');
  expect(sandbox).toBe('allow-popups allow-popups-to-escape-sandbox');

  const frame = page.frameLocator('iframe[title="Hostile sample"]');
  await expect(frame.locator('#visible')).toContainText('Visible text');
  await frame.getByText('javascript link').click();
  await page.waitForTimeout(500);

  expect(await frame.locator('body').getAttribute('data-pwned')).toBeNull();
  expect(requests.filter((url) => url.includes('__pwned'))).toEqual([]);
  expect(dialogs).toEqual([]);

  const policy = await frame
    .locator('meta[http-equiv="Content-Security-Policy"]')
    .getAttribute('content');
  expect(policy).toContain("script-src 'none'");
});
