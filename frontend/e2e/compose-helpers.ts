import type { Page } from '@playwright/test';

interface Folder {
  path: string;
  role: string;
}

export interface ListedMessage {
  id: string;
  subject: string;
}

/** The messages of the folder with the given role (inbox, sent, drafts, ...), read through the API of the signed-in session. */
export async function listFolder(page: Page, role: string): Promise<ListedMessage[]> {
  return page.evaluate(async (wanted) => {
    const folders = ((await (await fetch('/api/v1/folders')).json()) as { folders: Folder[] })
      .folders;
    const folder = folders.find((f) => f.role === wanted);
    if (!folder) return [];
    const response = await fetch(
      `/api/v1/folders/${encodeURIComponent(folder.path)}/messages?limit=100&preview=false`,
    );
    return ((await response.json()) as { items: ListedMessage[] }).items;
  }, role);
}

/** Deletes the messages with this subject for good (to the trash, then out of it) from the folders with the given roles. */
export async function deleteBySubject(
  page: Page,
  subject: string,
  roles: string[] = ['sent', 'inbox', 'drafts'],
): Promise<void> {
  for (const role of roles) {
    for (const item of (await listFolder(page, role)).filter((i) => i.subject === subject)) {
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

/** Takes back everything still waiting in the queue of scheduled messages (each goes back to Drafts), and removes the notes of failures. */
export async function emptyScheduled(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const { items } = (await (await fetch('/api/v1/scheduled')).json()) as {
      items: { id: string }[];
    };
    const { token } = (await (await fetch('/api/v1/csrf')).json()) as { token: string };
    for (const item of items) {
      await fetch(`/api/v1/scheduled/${item.id}`, {
        method: 'DELETE',
        headers: { 'X-CSRF-Token': token },
      });
    }
  });
}
