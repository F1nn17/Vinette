/** A person a message goes to. */
export interface Recipient {
  /** What the person is called; empty when only the address is known. */
  name: string;
  address: string;
}

/** The longest address mail allows. */
const MAX_ADDRESS_LENGTH = 254;

// A plain `local@domain`: no spaces, brackets, quotes, commas or other characters the server would refuse in a header.
const ADDRESS = /^[^\s@<>()[\]\\,;:"]+@[^\s@<>()[\]\\,;:"]+$/;

/** Whether the text is an address the server accepts: a plain `local@domain`. */
export function isValidAddress(address: string): boolean {
  return address.length <= MAX_ADDRESS_LENGTH && ADDRESS.test(address);
}

/** Cuts the text at commas, semicolons and line breaks that are not inside quotes or `<...>`. */
function splitList(text: string): string[] {
  const pieces: string[] = [];
  let current = '';
  let quoted = false;
  let angled = false;
  for (const ch of text) {
    if (ch === '"' && !angled) quoted = !quoted;
    else if (ch === '<' && !quoted) angled = true;
    else if (ch === '>' && !quoted) angled = false;
    if (!quoted && !angled && (ch === ',' || ch === ';' || ch === '\n' || ch === '\r')) {
      pieces.push(current);
      current = '';
    } else {
      current += ch;
    }
  }
  pieces.push(current);
  return pieces.map((p) => p.trim()).filter(Boolean);
}

const unquote = (name: string) =>
  name
    .trim()
    .replace(/^"(.*)"$/s, '$1')
    .replace(/\\(.)/g, '$1')
    .trim();

/**
 * The people in text typed or pasted: `a@x.test`, `Anna <a@x.test>`, `"Smirnova, Anna" <a@x.test>`, or several of them separated by commas,
 * semicolons, line breaks or (for bare addresses) spaces. Nothing is dropped, even when it is not an address: the field shows it as a
 * mistake the user can see and remove. The same address twice (any case) is one person.
 */
export function parseRecipients(text: string): Recipient[] {
  const found: Recipient[] = [];
  for (const piece of splitList(text)) {
    const angled = /^(.*)<([^<>]*)>\s*$/s.exec(piece);
    if (angled) {
      found.push({ name: unquote(angled[1] ?? ''), address: (angled[2] ?? '').trim() });
      continue;
    }
    const words = piece.split(/\s+/);
    if (words.length > 1 && words.every((w) => w.includes('@'))) {
      for (const w of words) found.push({ name: '', address: w });
    } else {
      found.push({ name: '', address: piece });
    }
  }
  return withoutDuplicates(found);
}

/** The people without a second copy of an address (any case); the first one stays. */
export function withoutDuplicates(people: Recipient[]): Recipient[] {
  const seen = new Set<string>();
  return people.filter((p) => {
    const key = p.address.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** Adds the people to the list, skipping the addresses it already has. */
export function addRecipients(list: Recipient[], added: Recipient[]): Recipient[] {
  return withoutDuplicates([...list, ...added]);
}

/** What a person is shown as: the name, or the address when there is no name. */
export const recipientLabel = (r: Recipient) => (r.name === '' ? r.address : r.name);
