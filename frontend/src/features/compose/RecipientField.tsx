import { X } from 'lucide-react';
import { useId, useRef, useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/shared/ui';
import { cn } from '@/shared/lib/cn';
import {
  addRecipients,
  isValidAddress,
  parseRecipients,
  recipientLabel,
  type Recipient,
} from './recipients';
import { useRecipientSuggestions } from './suggestions';

export interface RecipientFieldProps {
  /** Id of the text input, so that the row label points at it. */
  id: string;
  /** The name of the row ("To", "Cc"): names the list of people for assistive technology. */
  label: string;
  value: Recipient[];
  onChange: (value: Recipient[]) => void;
  autoFocus?: boolean;
}

/**
 * The people a message goes to (DESIGN.md §6): a chip with an avatar and a remove button for each, and a text input that turns what is
 * typed or pasted into chips (at Enter, a comma, a semicolon, a line break or when the cursor leaves). An address the server would
 * refuse stays as a chip marked as a mistake. The people the mailbox deals with are offered while typing.
 */
export function RecipientField({ id, label, value, onChange, autoFocus }: RecipientFieldProps) {
  const { t } = useTranslation();
  const listId = useId();
  const input = useRef<HTMLInputElement>(null);
  const [text, setText] = useState('');
  const [active, setActive] = useState(-1);
  const [dismissed, setDismissed] = useState(false);
  const have = new Set(value.map((r) => r.address.toLowerCase()));
  const offered = useRecipientSuggestions(text).filter((s) => !have.has(s.address.toLowerCase()));
  const open = !dismissed && offered.length > 0;
  const current = open && active >= 0 && active < offered.length ? active : -1;

  const take = (people: Recipient[]) => {
    if (people.length > 0) onChange(addRecipients(value, people));
    setText('');
    setActive(-1);
    setDismissed(false);
  };
  const commit = (raw: string) => take(parseRecipients(raw));
  const pick = (person: Recipient) => {
    take([person]);
    input.current?.focus();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.nativeEvent.isComposing) return;
    // Ctrl+Enter sends the message: it is not for this field (what is typed here is taken in as the cursor leaves).
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) return;
    switch (e.key) {
      case 'ArrowDown':
        if (open) {
          e.preventDefault();
          setActive((current + 1) % offered.length);
        }
        break;
      case 'ArrowUp':
        if (open) {
          e.preventDefault();
          setActive(current <= 0 ? offered.length - 1 : current - 1);
        }
        break;
      case 'Escape':
        if (open) {
          e.preventDefault();
          setDismissed(true);
        }
        break;
      case 'Enter': {
        // Enter never sends the message from here; it takes the offered person or what was typed.
        e.preventDefault();
        const chosen = current >= 0 ? offered[current] : undefined;
        if (chosen) pick(chosen);
        else if (text.trim() !== '') commit(text);
        break;
      }
      case 'Tab': {
        const chosen = current >= 0 ? offered[current] : undefined;
        if (chosen) {
          e.preventDefault();
          pick(chosen);
        } else if (text.trim() !== '') {
          commit(text);
        }
        break;
      }
      case 'Backspace':
        if (text === '' && value.length > 0) onChange(value.slice(0, -1));
        break;
    }
  };

  const onPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    const pasted = e.clipboardData.getData('text');
    // One word is just text for the input; a list becomes chips at once.
    if (/[\s,;]/.test(pasted.trim())) {
      e.preventDefault();
      commit(text + pasted);
    }
  };

  return (
    <div className="relative flex min-w-0 flex-1 flex-wrap items-center gap-1.5 py-2">
      <ul aria-label={t('compose.recipientList', { row: label })} className="contents">
        {value.map((r) => {
          const bad = !isValidAddress(r.address);
          return (
            <li
              key={r.address}
              className={cn(
                'inline-flex h-8 max-w-full items-center gap-1.5 rounded-pill py-1 pr-1 pl-1 text-secondary',
                bad ? 'bg-warning-bg text-warning' : 'bg-bg text-ink',
              )}
            >
              {!bad && (
                <span aria-hidden>
                  <Avatar name={recipientLabel(r)} size={24} />
                </span>
              )}
              <span className={cn('truncate', bad && 'pl-2')} title={r.address}>
                {recipientLabel(r)}
              </span>
              {bad && <span className="sr-only">{t('compose.invalidAddress')}</span>}
              <button
                type="button"
                aria-label={t('compose.removeRecipient', { name: recipientLabel(r) })}
                onClick={() => {
                  onChange(value.filter((x) => x !== r));
                  input.current?.focus();
                }}
                className="relative inline-flex size-6 shrink-0 items-center justify-center rounded-full hover:bg-surface max-md:after:absolute max-md:after:-inset-2.5 max-md:after:content-['']"
              >
                <X size={14} strokeWidth={1.8} aria-hidden />
              </button>
            </li>
          );
        })}
      </ul>
      <input
        ref={input}
        id={id}
        type="text"
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={current >= 0 ? `${listId}-${current}` : undefined}
        inputMode="email"
        autoComplete="off"
        autoFocus={autoFocus}
        value={text}
        onChange={(e) => {
          const next = e.target.value;
          setDismissed(false);
          setActive(-1);
          // A comma or a semicolon ends a recipient.
          if (/[,;]\s*$/.test(next)) commit(next);
          else setText(next);
        }}
        onKeyDown={onKeyDown}
        onPaste={onPaste}
        onBlur={() => {
          if (text.trim() !== '') commit(text);
        }}
        className="min-w-32 flex-1 bg-transparent py-1 text-base text-ink placeholder:text-muted focus:outline-none"
      />
      <ul
        id={listId}
        role="listbox"
        aria-label={t('compose.suggestions')}
        hidden={!open}
        className="absolute top-full left-0 z-10 mt-1 flex max-h-72 w-96 max-w-full flex-col overflow-y-auto rounded-md border border-line bg-surface p-1 shadow-card"
      >
        {offered.map((s, i) => (
          <li
            key={s.address}
            id={`${listId}-${i}`}
            role="option"
            aria-selected={i === current}
            // The mouse must not take the focus from the input before the choice is made.
            onMouseDown={(e) => {
              e.preventDefault();
              pick(s);
            }}
            className={cn(
              'flex min-h-11 cursor-pointer items-center gap-3 rounded-sm px-2 py-1',
              i === current ? 'bg-accent-soft' : 'hover:bg-bg',
            )}
          >
            <Avatar name={recipientLabel(s)} size={28} />
            <span className="flex min-w-0 flex-col">
              {s.name !== '' && <span className="truncate text-base text-ink">{s.name}</span>}
              <span className="truncate text-meta text-muted">{s.address}</span>
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
