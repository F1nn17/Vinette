import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { shortcutOf, useShortcuts, type ShortcutHandlers } from './shortcuts';

const press = (init: Partial<KeyboardEventInit>, target: EventTarget | null = document.body) => {
  const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
  Object.defineProperty(event, 'target', { value: target });
  return shortcutOf(event);
};

describe('shortcutOf', () => {
  it.each([
    [{ key: 'j', code: 'KeyJ' }, 'next'],
    [{ key: 'k', code: 'KeyK' }, 'previous'],
    [{ key: 'Enter', code: 'Enter' }, 'open'],
    [{ key: 'e', code: 'KeyE' }, 'archive'],
    [{ key: '#', code: 'Digit3', shiftKey: true }, 'delete'],
    [{ key: '/', code: 'Slash' }, 'search'],
    [{ key: 'c', code: 'KeyC' }, 'compose'],
    [{ key: '?', code: 'Slash', shiftKey: true }, 'help'],
  ] as const)('%j is "%s"', (init, name) => {
    expect(press(init)).toBe(name);
  });

  it('works in the Russian layout: keys are read by their place on the keyboard', () => {
    expect(press({ key: 'о', code: 'KeyJ' })).toBe('next');
    expect(press({ key: 'л', code: 'KeyK' })).toBe('previous');
    expect(press({ key: 'у', code: 'KeyE' })).toBe('archive');
    expect(press({ key: 'с', code: 'KeyC' })).toBe('compose');
    expect(press({ key: '№', code: 'Digit3', shiftKey: true })).toBe('delete');
    expect(press({ key: '?', code: 'Digit7', shiftKey: true })).toBe('help');
  });

  it('ignores keys that mean nothing, and capitals', () => {
    expect(press({ key: 'x', code: 'KeyX' })).toBeNull();
    expect(press({ key: 'J', code: 'KeyJ', shiftKey: true })).toBeNull();
    expect(press({ key: 'Escape', code: 'Escape' })).toBeNull();
  });

  it.each([{ ctrlKey: true }, { altKey: true }, { metaKey: true }])(
    'leaves the key to the browser with %j held',
    (held) => {
      expect(press({ key: 'e', code: 'KeyE', ...held })).toBeNull();
      expect(press({ key: 'c', code: 'KeyC', ...held })).toBeNull();
    },
  );

  it('does nothing while an input method is composing text', () => {
    expect(press({ key: 'j', code: 'KeyJ', isComposing: true })).toBeNull();
  });

  it.each(['input', 'textarea', 'select'])('does nothing while typing in a %s', (tag) => {
    const field = document.createElement(tag);
    document.body.append(field);
    try {
      expect(press({ key: 'j', code: 'KeyJ' }, field)).toBeNull();
      expect(press({ key: '/', code: 'Slash' }, field)).toBeNull();
      expect(press({ key: '#', code: 'Digit3', shiftKey: true }, field)).toBeNull();
    } finally {
      field.remove();
    }
  });

  it.each(['checkbox', 'radio', 'button'])(
    'still works with the focus on a %s (nothing is typed there)',
    (type) => {
      const input = document.createElement('input');
      input.type = type;
      document.body.append(input);
      try {
        expect(press({ key: 'j', code: 'KeyJ' }, input)).toBe('next');
        expect(press({ key: '#', code: 'Digit3', shiftKey: true }, input)).toBe('delete');
      } finally {
        input.remove();
      }
    },
  );

  it.each(['text', 'search', 'email', 'password', 'number'])(
    'does nothing while typing in an input of type %s',
    (type) => {
      const input = document.createElement('input');
      input.type = type;
      document.body.append(input);
      try {
        expect(press({ key: 'j', code: 'KeyJ' }, input)).toBeNull();
      } finally {
        input.remove();
      }
    },
  );

  it('does nothing in an editable area', () => {
    const area = document.createElement('div');
    area.setAttribute('contenteditable', 'true'); // jsdom has no contentEditable property
    document.body.append(area);
    try {
      expect(press({ key: 'e', code: 'KeyE' }, area)).toBeNull();
    } finally {
      area.remove();
    }
  });

  it('does nothing while a window is open over the screen', () => {
    const dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    document.body.append(dialog);
    try {
      expect(press({ key: 'j', code: 'KeyJ' })).toBeNull();
      expect(press({ key: '?', code: 'Slash', shiftKey: true })).toBeNull();
    } finally {
      dialog.remove();
    }
  });

  it('leaves Enter to a link, a button or a box, but letters still work there', () => {
    for (const tag of ['a', 'button']) {
      const element = document.createElement(tag);
      document.body.append(element);
      try {
        expect(press({ key: 'Enter', code: 'Enter' }, element)).toBeNull();
        expect(press({ key: 'j', code: 'KeyJ' }, element)).toBe('next');
      } finally {
        element.remove();
      }
    }
  });

  it('does nothing when something already handled the key', () => {
    expect(press({ key: 'j', code: 'KeyJ' }, document.body)).toBe('next');
    const event = new KeyboardEvent('keydown', { key: 'j', code: 'KeyJ', cancelable: true });
    event.preventDefault();
    expect(shortcutOf(event)).toBeNull();
  });
});

describe('useShortcuts', () => {
  function Listener({ handlers }: { handlers: ShortcutHandlers }) {
    useShortcuts(handlers);
    return <input aria-label="field" />;
  }

  it('calls the handler of the key, and takes the key (no default action)', async () => {
    const next = vi.fn();
    const user = userEvent.setup();
    render(<Listener handlers={{ next }} />);

    await user.keyboard('j');

    expect(next).toHaveBeenCalledOnce();
  });

  it('leaves a key without a handler alone', () => {
    render(<Listener handlers={{}} />);
    const event = new KeyboardEvent('keydown', {
      key: 'j',
      code: 'KeyJ',
      bubbles: true,
      cancelable: true,
    });

    document.body.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });

  it('is silent while the user types in a field', async () => {
    const next = vi.fn();
    const user = userEvent.setup();
    render(<Listener handlers={{ next }} />);

    await user.click(screen.getByLabelText('field'));
    await user.keyboard('jjj');

    expect(next).not.toHaveBeenCalled();
  });

  it('always calls the latest handler, without listening again', async () => {
    const first = vi.fn();
    const second = vi.fn();
    const user = userEvent.setup();
    const { rerender } = render(<Listener handlers={{ next: first }} />);

    rerender(<Listener handlers={{ next: second }} />);
    await user.keyboard('j');

    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledOnce();
  });

  it('stops listening when the component goes away', async () => {
    const next = vi.fn();
    const user = userEvent.setup();
    const { unmount } = render(<Listener handlers={{ next }} />);

    unmount();
    await user.keyboard('j');

    expect(next).not.toHaveBeenCalled();
  });
});
