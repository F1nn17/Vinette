import type { components } from '@/shared/api/schema';

type Folder = components['schemas']['FolderDto'];
type Translate = (key: string) => string;

const SPECIAL_ROLES = new Set(['inbox', 'drafts', 'sent', 'archive', 'junk', 'trash']);

/**
 * The name of a folder for the user: the special folders (inbox, drafts, sent, archive, spam, trash) get a name in the
 * interface language whatever the mail server calls them; the user's own folders keep their own name.
 */
export function folderLabel(folder: Pick<Folder, 'name' | 'role'>, t: Translate): string {
  return SPECIAL_ROLES.has(folder.role) ? t(`folders.${folder.role}`) : folder.name;
}
