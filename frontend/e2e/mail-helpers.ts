import { execFileSync } from 'node:child_process';
import type { Page } from '@playwright/test';

// Helpers of the e2e scenarios that need mail to arrive or vanish behind the screen's back (real-time, recovery): the real backend and
// a real Dovecot (`make dev-mail`); a mail is put into a mailbox inside the mail server's container, the way a delivery stores it
// (the path over SMTP is covered by the backend's integration tests).

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
export function deliver(to: string, subject: string): Promise<void> {
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

export interface Item {
  id: string;
  subject: string;
}

export async function inbox(page: Page): Promise<Item[]> {
  return page.evaluate(async () => {
    const response = await fetch('/api/v1/folders/INBOX/messages?limit=100&preview=false');
    return ((await response.json()) as { items: Item[] }).items;
  });
}

/** Removes the mail for good: to the trash, then out of it. */
export async function remove(page: Page, subject: string): Promise<void> {
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
