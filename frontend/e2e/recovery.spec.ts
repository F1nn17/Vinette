import { expect, test } from '@playwright/test';
import { accounts, signIn, signOutViaApi } from './helpers';
import { deliver, remove } from './mail-helpers';

// Recovery after a lost connection, end to end: the network goes away, the screen says so and keeps what it has, mail arrives
// meanwhile, the network comes back, and the list is up to date again without anyone doing anything. The mail is deleted again.

test.describe('recovery', () => {
  const subject = `Recovery ${Date.now()}`;

  test.beforeEach(async ({ page }) => {
    await signIn(page, accounts.alice);
  });

  test.afterEach(async ({ page, context }) => {
    await context.setOffline(false);
    await remove(page, subject);
    await signOutViaApi(page);
  });

  test('the network goes away and comes back: a notice, then a list that is up to date', async ({
    page,
    context,
  }) => {
    await page.goto('/mail/inbox');
    const sidebar = page.getByRole('navigation', { name: 'Папки' });
    await expect(sidebar.getByRole('link', { name: /Входящие/ })).toBeVisible();
    const firstRow = page.locator('a[href^="/mail/inbox/"]').first();
    await expect(firstRow).toBeVisible();
    await page.waitForTimeout(2000); // the live channel settles

    await context.setOffline(true);
    await expect(page.getByText(/Нет связи/)).toBeVisible({ timeout: 15_000 });
    // what was loaded stays on the screen
    await expect(firstRow).toBeVisible();

    await deliver(accounts.alice.address, subject); // arrives while the screen cannot hear about it

    await context.setOffline(false);

    await expect(page.getByText(/Нет связи/)).toHaveCount(0, { timeout: 45_000 });
    await expect(page.getByRole('link', { name: new RegExp(subject) })).toBeVisible({
      timeout: 45_000,
    });
  });

  test('the live channel is cut while the browser still thinks it has a network: the notice comes, and goes when it is back', async ({
    page,
  }) => {
    // Only the channel of updates is broken (a proxy that does not let WebSockets through, a server that restarts): requests still work.
    let blocked = false;
    const sockets: { close: () => void }[] = [];
    await page.route('**/hubs/**', (route) => (blocked ? route.abort() : route.continue()));
    await page.routeWebSocket(/\/hubs\/mail/, (ws) => {
      if (blocked) {
        void ws.close();
        return;
      }
      ws.connectToServer();
      sockets.push({ close: () => void ws.close() });
    });
    await page.goto('/mail/inbox');
    const firstRow = page.locator('a[href^="/mail/inbox/"]').first();
    await expect(firstRow).toBeVisible();
    await page.waitForTimeout(2000);
    await expect(page.getByText(/Нет связи/)).toHaveCount(0);

    blocked = true;
    sockets.forEach((socket) => socket.close());
    await expect(page.getByText(/Нет связи/)).toBeVisible({ timeout: 30_000 });
    await expect(firstRow).toBeVisible(); // what was loaded stays

    await deliver(accounts.alice.address, subject);
    blocked = false;

    await expect(page.getByText(/Нет связи/)).toHaveCount(0, { timeout: 60_000 });
    await expect(page.getByRole('link', { name: new RegExp(subject) })).toBeVisible({
      timeout: 60_000,
    });
  });
});
