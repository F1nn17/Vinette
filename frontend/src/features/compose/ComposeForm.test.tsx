import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { EMPTY_COMPOSE, type ComposeValues } from './compose-values';
import { ComposeForm } from './ComposeForm';

function Harness({
  initial = EMPTY_COMPOSE,
  onChange,
}: {
  initial?: ComposeValues;
  onChange?: (v: ComposeValues) => void;
}) {
  const [values, setValues] = useState(initial);
  const [client] = useState(() => new QueryClient());
  return (
    <QueryClientProvider client={client}>
      <ComposeForm
        address="user@example.com"
        values={values}
        onChange={(v) => {
          setValues(v);
          onChange?.(v);
        }}
      />
    </QueryClientProvider>
  );
}

describe('ComposeForm', () => {
  it('has the sender, the recipients and the subject, each with a label', () => {
    render(<Harness />);

    expect(screen.getByRole('form', { name: 'Письмо' })).toBeInTheDocument();
    expect(screen.getByLabelText('От')).toHaveValue('user@example.com');
    expect(screen.getByLabelText('Кому')).toHaveValue('');
    expect(screen.getByLabelText('Тема')).toHaveValue('');
    expect(screen.queryByLabelText('Копия')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Скрытая')).not.toBeInTheDocument();
  });

  it('passes what is typed on, keeping the other fields', async () => {
    const seen: ComposeValues[] = [];
    render(<Harness onChange={(v) => seen.push(v)} />);

    await userEvent.type(screen.getByLabelText('Кому'), 'a@x.test{Enter}');
    await userEvent.type(screen.getByLabelText('Тема'), 'Hi');

    expect(seen.at(-1)).toEqual({
      to: [{ name: '', address: 'a@x.test' }],
      cc: [],
      bcc: [],
      subject: 'Hi',
    });
  });

  it('opens the Cc and Bcc rows on demand and puts the cursor in them', async () => {
    render(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Копия: добавить получателей' }));

    const cc = screen.getByLabelText('Копия');
    expect(cc).toHaveFocus();
    expect(
      screen.queryByRole('button', { name: 'Копия: добавить получателей' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Скрытая')).not.toBeInTheDocument();

    await userEvent.click(
      screen.getByRole('button', { name: 'Скрытая: добавить получателей в скрытую копию' }),
    );
    expect(screen.getByLabelText('Скрытая')).toHaveFocus();
  });

  it('can close an empty extra row, but not one that holds recipients', async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole('button', { name: 'Копия: добавить получателей' }));

    await userEvent.click(screen.getByRole('button', { name: 'Убрать строку «Копия»' }));
    expect(screen.queryByLabelText('Копия')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Копия: добавить получателей' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Копия: добавить получателей' }));
    await userEvent.type(screen.getByLabelText('Копия'), 'b@x.test{Enter}');
    expect(screen.queryByRole('button', { name: 'Убрать строку «Копия»' })).not.toBeInTheDocument();
  });

  it('opens with a row already open when it holds recipients (a restored draft)', () => {
    render(
      <Harness initial={{ ...EMPTY_COMPOSE, bcc: [{ name: '', address: 'hidden@x.test' }] }} />,
    );

    expect(screen.getByRole('list', { name: 'Получатели: Скрытая' })).toHaveTextContent(
      'hidden@x.test',
    );
    expect(
      screen.queryByRole('button', { name: 'Скрытая: добавить получателей в скрытую копию' }),
    ).not.toBeInTheDocument();
  });

  it('does not submit the page when Enter is pressed in a field', async () => {
    render(<Harness />);

    await userEvent.type(screen.getByLabelText('Тема'), 'x{Enter}');

    expect(screen.getByLabelText('Тема')).toHaveValue('x');
  });
});
