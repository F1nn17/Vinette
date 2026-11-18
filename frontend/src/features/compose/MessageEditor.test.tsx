import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { EMPTY_BODY, type ComposeBody } from './editor-text';
import { MessageEditor } from './MessageEditor';

// ProseMirror measures the page while it moves the cursor into view; jsdom has no layout.
beforeAll(() => {
  const rect = {
    x: 0,
    y: 0,
    width: 0,
    height: 0,
    top: 0,
    right: 0,
    bottom: 0,
    left: 0,
    toJSON: () => ({}),
  };
  Range.prototype.getBoundingClientRect = () => rect as DOMRect;
  Range.prototype.getClientRects = () =>
    ({
      length: 0,
      item: () => null,
      [Symbol.iterator]: [][Symbol.iterator],
    }) as unknown as DOMRectList;
  document.elementFromPoint = () => null;
});

function Harness({ initial = EMPTY_BODY, seen }: { initial?: ComposeBody; seen?: ComposeBody[] }) {
  const [value, setValue] = useState(initial);
  return (
    <MessageEditor
      value={value}
      onChange={(v) => {
        setValue(v);
        seen?.push(v);
      }}
    />
  );
}

const text = () => screen.getByRole('textbox', { name: 'Текст письма' });

describe('MessageEditor', () => {
  it('has a labelled toolbar and a text area, each button with a name', async () => {
    render(<Harness />);

    await screen.findByRole('textbox', { name: 'Текст письма' });
    const bar = screen.getByRole('toolbar', { name: 'Форматирование' });
    for (const name of [
      'Жирный',
      'Курсив',
      'Подчёркнутый',
      'Маркированный список',
      'Нумерованный список',
      'Цитата',
      'Ссылка',
      'Код',
    ]) {
      expect(screen.getByRole('button', { name })).toBeInTheDocument();
    }
    expect(bar).toContainElement(screen.getByRole('button', { name: 'Простой текст' }));
    expect(text()).toHaveAttribute('aria-multiline', 'true');
  });

  it('reports what is typed as html and as text', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);

    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));
    await userEvent.keyboard('Hello{Enter}world');

    expect(seen.at(-1)).toEqual({
      plain: false,
      html: '<p>Hello</p><p>world</p>',
      text: 'Hello\nworld',
    });
  });

  it('formats with the buttons and shows which formats are on', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));

    await userEvent.click(screen.getByRole('button', { name: 'Жирный' }));
    expect(screen.getByRole('button', { name: 'Жирный' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.keyboard('bold');
    await userEvent.click(screen.getByRole('button', { name: 'Жирный' }));
    expect(screen.getByRole('button', { name: 'Жирный' })).toHaveAttribute('aria-pressed', 'false');
    await userEvent.keyboard(' plain');

    expect(seen.at(-1)?.html).toBe('<p><strong>bold</strong> plain</p>');
  });

  it('makes a list with its button', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));

    await userEvent.click(screen.getByRole('button', { name: 'Маркированный список' }));
    await userEvent.keyboard('item');

    expect(screen.getByRole('button', { name: 'Маркированный список' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(seen.at(-1)?.html).toContain('<ul><li><p>item</p></li></ul>');
  });

  it('formats with the keyboard', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));

    await userEvent.keyboard('{Control>}i{/Control}it');

    expect(seen.at(-1)?.html).toBe('<p><em>it</em></p>');
  });

  it('puts a link on the text, refusing an address that is not a web or mail one', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));

    await userEvent.click(screen.getByRole('button', { name: 'Ссылка' }));
    const address = screen.getByLabelText('Адрес ссылки');
    expect(address).toHaveFocus();
    await userEvent.type(address, 'javascript:alert(1){Enter}');

    expect(screen.getByRole('alert')).toHaveTextContent('Нужен адрес сайта или адрес почты.');
    expect(seen.at(-1)?.html ?? '').not.toContain('javascript');

    await userEvent.clear(address);
    await userEvent.type(address, 'example.org{Enter}');

    expect(screen.queryByLabelText('Адрес ссылки')).not.toBeInTheDocument();
    expect(seen.at(-1)?.html).toContain('href="https://example.org"');
    expect(seen.at(-1)?.html).toContain('rel="noopener noreferrer nofollow"');
  });

  it('closes the link address with Escape', async () => {
    render(<Harness />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));
    await userEvent.click(screen.getByRole('button', { name: 'Ссылка' }));

    await userEvent.keyboard('{Escape}');

    expect(screen.queryByLabelText('Адрес ссылки')).not.toBeInTheDocument();
  });

  it('drops the formatting in plain text mode and brings it back when the text is untouched', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));
    await userEvent.click(screen.getByRole('button', { name: 'Жирный' }));
    await userEvent.keyboard('Hi');

    await userEvent.click(screen.getByRole('button', { name: 'Простой текст' }));

    const field = screen.getByRole('textbox', { name: 'Текст письма' });
    expect(field.tagName).toBe('TEXTAREA');
    expect(field).toHaveValue('Hi');
    expect(screen.queryByRole('button', { name: 'Жирный' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Простой текст' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(seen.at(-1)).toEqual({ plain: true, html: '', text: 'Hi' });

    await userEvent.click(screen.getByRole('button', { name: 'Простой текст' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Жирный' })).toBeInTheDocument());
    expect(seen.at(-1)).toEqual({ plain: false, html: '<p><strong>Hi</strong></p>', text: 'Hi' });
  });

  it('builds paragraphs from the text that was changed in plain text mode, never taking it for markup', async () => {
    const seen: ComposeBody[] = [];
    render(<Harness seen={seen} />);
    await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));
    await userEvent.keyboard('Hi');
    await userEvent.click(screen.getByRole('button', { name: 'Простой текст' }));

    const field = screen.getByRole('textbox', { name: 'Текст письма' });
    await userEvent.type(field, '{Enter}<b>x</b>');
    await userEvent.click(screen.getByRole('button', { name: 'Простой текст' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Жирный' })).toBeInTheDocument());
    expect(seen.at(-1)?.html).toBe('<p>Hi</p><p>&lt;b&gt;x&lt;/b&gt;</p>');
  });

  it('starts with the formatted body it is given', async () => {
    render(
      <Harness
        initial={{ plain: false, html: '<p>Hello <strong>you</strong></p>', text: 'Hello you' }}
      />,
    );

    expect(await screen.findByText('you')).toBeInTheDocument();
    expect(text()).toHaveTextContent('Hello you');
  });
});
