import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { RecipientField } from './RecipientField';
import type { Recipient } from './recipients';

const ANNA = { name: 'Anna Smirnova', address: 'anna@studio.example' };
const BORIS = { name: '', address: 'boris@example.org' };

let asked: string[];

beforeEach(() => {
  asked = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      asked.push(decodeURIComponent(String(url)));
      return Promise.resolve(
        new Response(JSON.stringify({ items: [ANNA, BORIS] }), {
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function Harness({
  initial = [],
  onChange,
}: {
  initial?: Recipient[];
  onChange?: (v: Recipient[]) => void;
}) {
  const [value, setValue] = useState<Recipient[]>(initial);
  const [client] = useState(
    () => new QueryClient({ defaultOptions: { queries: { retry: false } } }),
  );
  return (
    <QueryClientProvider client={client}>
      <label htmlFor="to">Кому</label>
      <RecipientField
        id="to"
        label="Кому"
        value={value}
        onChange={(v) => {
          setValue(v);
          onChange?.(v);
        }}
      />
    </QueryClientProvider>
  );
}

const chips = () => screen.getByRole('list', { name: 'Получатели: Кому' }).querySelectorAll('li');

describe('RecipientField', () => {
  it('turns an address into a chip at Enter, a comma, a semicolon and when the cursor leaves', async () => {
    render(<Harness />);
    const field = screen.getByLabelText('Кому');

    await userEvent.type(field, 'a@x.test{Enter}');
    await userEvent.type(field, 'b@x.test,');
    await userEvent.type(field, 'c@x.test;');
    await userEvent.type(field, 'd@x.test');
    await userEvent.tab();

    expect([...chips()].map((c) => c.querySelector('[title]')?.textContent)).toEqual([
      'a@x.test',
      'b@x.test',
      'c@x.test',
      'd@x.test',
    ]);
    expect(field).toHaveValue('');
  });

  it('turns a pasted list into chips at once, with names, without a second copy of an address', async () => {
    const seen: Recipient[][] = [];
    render(<Harness onChange={(v) => seen.push(v)} />);

    await userEvent.click(screen.getByLabelText('Кому'));
    await userEvent.paste('Anna <a@x.test>, b@x.test\nB@X.test; c@x.test');

    expect(seen.at(-1)).toEqual([
      { name: 'Anna', address: 'a@x.test' },
      { name: '', address: 'b@x.test' },
      { name: '', address: 'c@x.test' },
    ]);
    expect(screen.getByLabelText('Кому')).toHaveValue('');
  });

  it('leaves a single pasted word in the input', async () => {
    render(<Harness />);

    await userEvent.click(screen.getByLabelText('Кому'));
    await userEvent.paste('anna@x.test');

    expect(screen.getByLabelText('Кому')).toHaveValue('anna@x.test');
    expect(chips()).toHaveLength(0);
  });

  it('marks an address that is not one, and says so to assistive technology', async () => {
    render(<Harness />);

    await userEvent.type(screen.getByLabelText('Кому'), 'anna{Enter}');

    const chip = chips()[0]!;
    expect(chip).toHaveTextContent('anna');
    expect(chip).toHaveTextContent('Неверный адрес');
  });

  it('removes a chip with its button, which names the person', async () => {
    const seen: Recipient[][] = [];
    render(<Harness initial={[ANNA, BORIS]} onChange={(v) => seen.push(v)} />);

    await userEvent.click(screen.getByRole('button', { name: 'Убрать: Anna Smirnova' }));

    expect(seen.at(-1)).toEqual([BORIS]);
    expect(screen.getByLabelText('Кому')).toHaveFocus();
  });

  it('removes the last chip with Backspace in an empty input, and only then', async () => {
    const seen: Recipient[][] = [];
    render(<Harness initial={[ANNA, BORIS]} onChange={(v) => seen.push(v)} />);
    const field = screen.getByLabelText('Кому');

    await userEvent.type(field, 'x{Backspace}');
    expect(seen).toHaveLength(0);

    await userEvent.type(field, '{Backspace}');
    expect(seen.at(-1)).toEqual([ANNA]);
  });

  it('offers people while typing and takes one with the keyboard', async () => {
    const seen: Recipient[][] = [];
    render(<Harness onChange={(v) => seen.push(v)} />);
    const field = screen.getByLabelText('Кому');

    await userEvent.type(field, 'an');

    expect(await screen.findByRole('option', { name: /Anna Smirnova/ })).toBeInTheDocument();
    expect(field).toHaveAttribute('aria-expanded', 'true');
    expect(asked.at(-1)).toBe('/api/v1/recipients/suggestions?q=an');

    await userEvent.keyboard('{ArrowDown}{ArrowDown}');
    expect(field).toHaveAttribute(
      'aria-activedescendant',
      screen.getByRole('option', { name: /boris@example.org/ }).id,
    );
    await userEvent.keyboard('{Enter}');

    expect(seen.at(-1)).toEqual([BORIS]);
    expect(field).toHaveValue('');
  });

  it('takes a person that is clicked, and does not offer one that is already there', async () => {
    const seen: Recipient[][] = [];
    render(<Harness initial={[BORIS]} onChange={(v) => seen.push(v)} />);

    await userEvent.type(screen.getByLabelText('Кому'), 'a');
    await userEvent.click(await screen.findByRole('option', { name: /Anna Smirnova/ }));

    expect(screen.queryByRole('option', { name: /boris/ })).not.toBeInTheDocument();
    expect(seen.at(-1)).toEqual([BORIS, ANNA]);
  });

  it('closes the offers with Escape and offers nothing for an empty input', async () => {
    render(<Harness />);
    const field = screen.getByLabelText('Кому');

    await userEvent.type(field, 'an');
    await screen.findByRole('option', { name: /Anna Smirnova/ });
    await userEvent.keyboard('{Escape}');
    expect(field).toHaveAttribute('aria-expanded', 'false');

    await userEvent.clear(field);
    await waitFor(() => expect(screen.queryByRole('option')).not.toBeInTheDocument());
  });

  it('does not ask the server for every key: a pause is waited for', async () => {
    render(<Harness />);

    await userEvent.type(screen.getByLabelText('Кому'), 'anna');
    await screen.findByRole('option', { name: /Anna Smirnova/ });

    expect(asked).toEqual(['/api/v1/recipients/suggestions?q=anna']);
  });
});
