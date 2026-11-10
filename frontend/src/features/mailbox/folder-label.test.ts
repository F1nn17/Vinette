import { folderLabel } from './folder-label';

const t = (key: string) => `<${key}>`;

describe('folderLabel', () => {
  it.each(['inbox', 'drafts', 'sent', 'archive', 'junk', 'trash'])(
    'names the %s folder in the interface language, not as the server does',
    (role) => {
      expect(folderLabel({ name: 'Gesendet', role }, t)).toBe(`<folders.${role}>`);
    },
  );

  it('keeps the name of a folder of the user', () => {
    expect(folderLabel({ name: 'Работа', role: 'other' }, t)).toBe('Работа');
  });

  it('keeps the name when the role is one this version does not know', () => {
    expect(folderLabel({ name: 'Notes', role: 'something-new' }, t)).toBe('Notes');
  });
});
