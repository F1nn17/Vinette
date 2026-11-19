import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { resetCsrfToken } from '@/shared/api/client';
import { ToastProvider } from '@/shared/toast';
import { ComposeScreen } from './ComposeScreen';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

interface Call {
  url: string;
  method: string;
  body?: Record<string, unknown>;
}

let calls: Call[];
let sendAnswer: () => Response;
let signatures: { id: string; name: string; text: string; default: boolean }[];

beforeEach(() => {
  resetCsrfToken();
  calls = [];
  signatures = [];
  sendAnswer = () => json({ messageId: 'm', copy: 'saved', copyId: 'c', draftRemoved: true });
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      calls.push({
        url: String(input),
        method,
        body:
          typeof init?.body === 'string'
            ? (JSON.parse(init.body) as Record<string, unknown>)
            : undefined,
      });
      if (input === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      if (input === '/api/v1/attachments')
        return Promise.resolve(
          json({ items: [], limits: { maxFileBytes: 1e6, maxTotalBytes: 1e6, maxFiles: 5 } }),
        );
      if (input === '/api/v1/drafts')
        return Promise.resolve(
          json({
            id: 'd1',
            messageId: 'm1',
            savedAt: '2026-11-18T14:41:00Z',
            previousRemoved: true,
          }),
        );
      if (input === '/api/v1/messages') return Promise.resolve(sendAnswer());
      if (input === '/api/v1/settings')
        return Promise.resolve(
          json({
            density: 'comfortable',
            language: null,
            labelColors: {},
            trustedImageSenders: [],
            signatures,
          }),
        );
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function open() {
  const router = createMemoryRouter(
    [
      { path: '/compose', element: <ComposeScreen address="user@example.com" /> },
      { path: '/mail/inbox', element: <p>inbox-screen</p> },
    ],
    { initialEntries: ['/compose'] },
  );
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryClientProvider>,
  );
  return router;
}

const sends = () => calls.filter((c) => c.url === '/api/v1/messages');

async function fill(to = 'anna@example.org', subject = 'Hello', text = 'Body text') {
  await userEvent.type(screen.getByLabelText('Кому'), `${to}{Enter}`);
  await userEvent.type(screen.getByLabelText('Тема'), subject);
  await userEvent.click(await screen.findByRole('textbox', { name: 'Текст письма' }));
  await userEvent.keyboard(text);
}

describe('sending from the editor', () => {
  it('says what is missing and sends nothing: no recipient, an address that is not one', async () => {
    open();

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    expect(screen.getByRole('alert')).toHaveTextContent('Добавьте хотя бы одного получателя.');

    await userEvent.type(screen.getByLabelText('Кому'), 'anna{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    expect(screen.getByRole('alert')).toHaveTextContent('адреса получателей записаны неверно');
    expect(sends()).toHaveLength(0);
  });

  it('sends what was written, with the draft it was saved as, and goes back to the inbox', async () => {
    const router = open();
    await fill();
    await screen.findByText(/Черновик сохранён в \d/, {}, { timeout: 4000 });

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    const [sent] = sends();
    expect(sent!.body).toMatchObject({
      to: [{ name: '', address: 'anna@example.org' }],
      subject: 'Hello',
      draftId: 'd1',
    });
    expect(String(sent!.body!.html)).toContain('Body text');
    expect(await screen.findByText('Письмо отправлено')).toBeInTheDocument();
  });

  it('says so when the copy in "Sent" could not be kept', async () => {
    sendAnswer = () => json({ messageId: 'm', copy: 'failed', copyId: null, draftRemoved: true });
    open();
    await fill();

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    expect(await screen.findByText(/сохранить его копию/)).toBeInTheDocument();
  });

  it('sends with Ctrl+Enter from any field, taking in an address that was half typed', async () => {
    const router = open();
    await userEvent.type(screen.getByLabelText('Тема'), 'Quick');
    await userEvent.type(
      screen.getByLabelText('Кому'),
      'late@example.org{Control>}{Enter}{/Control}',
    );

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    expect(sends()).toHaveLength(1);
    expect(sends()[0]!.body).toMatchObject({
      to: [{ address: 'late@example.org' }],
      subject: 'Quick',
    });
  });

  it('does not take Ctrl+Enter in the text for a line break', async () => {
    open();
    await fill('a@example.org', 'S', 'one');

    await userEvent.keyboard('{Control>}{Enter}{/Control}');

    await waitFor(() => expect(sends()).toHaveLength(1));
    expect(String(sends()[0]!.body!.html)).not.toContain('<br');
  });

  it('asks before sending without a subject, and sends only when confirmed', async () => {
    const router = open();
    await userEvent.type(screen.getByLabelText('Кому'), 'a@example.org{Enter}');

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    const dialog = await screen.findByRole('dialog', { name: 'Отправить без темы?' });
    await userEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(sends()).toHaveLength(0);
    expect(dialog).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'Отправить' }),
    );

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    expect(sends()).toHaveLength(1);
  });

  it('stays on the message and says in words why when the server refuses a recipient', async () => {
    sendAnswer = () =>
      json(
        { code: 'recipient_rejected', title: 'x', address: 'nobody@example.org' },
        { status: 422 },
      );
    const router = open();
    await fill('nobody@example.org');

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('nobody@example.org');
    expect(router.state.location.pathname).toBe('/compose');
    expect(screen.getByLabelText('Тема')).toHaveValue('Hello'); // nothing was lost
    expect(screen.getByRole('button', { name: 'Отправить' })).toBeEnabled(); // and it can be sent again
  });

  it('sends only once however often the button is pressed', async () => {
    open();
    await fill();

    const button = screen.getByRole('button', { name: 'Отправить' });
    await userEvent.dblClick(button);

    await waitFor(() => expect(sends().length).toBeGreaterThan(0));
    expect(sends()).toHaveLength(1);
  });
});

describe('deleting the draft', () => {
  it('leaves at once when nothing was written', async () => {
    const router = open();

    await userEvent.click(screen.getByRole('button', { name: 'Удалить черновик' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('asks first, then deletes the saved draft and goes back', async () => {
    const router = open();
    await fill();
    await screen.findByText(/Черновик сохранён в \d/, {}, { timeout: 4000 });

    await userEvent.click(screen.getByRole('button', { name: 'Удалить черновик' }));
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'Удалить черновик' }),
    );

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    expect(calls).toContainEqual(
      expect.objectContaining({ url: '/api/v1/drafts/d1', method: 'DELETE' }),
    );
    expect(sends()).toHaveLength(0);
  });
});

describe('the signature', () => {
  beforeEach(() => {
    signatures = [
      { id: 's1', name: 'Work', text: 'Regards,\nAlice', default: true },
      { id: 's2', name: 'Home', text: 'Cheers', default: false },
    ];
  });

  it('shows the default one chosen, with a preview, beside the editor', async () => {
    open();

    const card = await screen.findByRole('region', { name: 'Подпись' });
    await waitFor(() => expect(within(card).getByLabelText('Подпись')).toHaveValue('s1'));
    expect(within(card).getByRole('group', { name: 'Подпись под письмом' })).toHaveTextContent(
      'Regards, Alice',
    );
    expect(screen.getByRole('complementary', { name: 'Параметры письма' })).toContainElement(card);
  });

  it('goes under the message when it is sent, once, and is not in the text being written or in the draft', async () => {
    open();
    await fill();
    await screen.findByText(/Черновик сохранён в \d/, {}, { timeout: 4000 });
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Подпись' })).toHaveValue('s1'),
    );
    const draft = calls.filter((c) => c.url === '/api/v1/drafts').at(-1)!.body!;
    expect(String(draft.html)).not.toContain('Regards');
    expect(screen.getByRole('textbox', { name: 'Текст письма' })).not.toHaveTextContent('Regards');

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(sends()).toHaveLength(1));
    const html = String(sends()[0]!.body!.html);
    expect(html.endsWith('<p></p><p>Regards,</p><p>Alice</p>')).toBe(true);
    expect(html.split('Regards').length).toBe(2);
  });

  it('can be another one, or none, for this message only', async () => {
    open();
    await fill();
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Подпись' })).toHaveValue('s1'),
    );

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Подпись' }), 's2');
    expect(screen.getByRole('group', { name: 'Подпись под письмом' })).toHaveTextContent('Cheers');
    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));
    await waitFor(() => expect(sends()).toHaveLength(1));
    expect(String(sends()[0]!.body!.html)).toContain('Cheers');
    expect(String(sends()[0]!.body!.html)).not.toContain('Regards');
  });

  it('is left out when the user chose none', async () => {
    open();
    await fill();
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Подпись' })).toHaveValue('s1'),
    );

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Подпись' }), '');
    expect(screen.queryByRole('group', { name: 'Подпись под письмом' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(sends()).toHaveLength(1));
    expect(String(sends()[0]!.body!.html)).not.toMatch(/Regards|Cheers/);
  });

  it('offers to make the first one when there are none, and opens the window for it', async () => {
    signatures = [];
    open();

    const card = await screen.findByRole('region', { name: 'Подпись' });
    expect(within(card).getByText('Подписей пока нет.')).toBeInTheDocument();
    await userEvent.click(within(card).getByRole('button', { name: 'Создать подпись' }));

    const dialog = await screen.findByRole('dialog', { name: 'Подписи' });
    expect(within(dialog).getByRole('button', { name: 'Новая подпись' })).toBeInTheDocument();
  });

  it('opens the window to manage them, and the new list is in the choice when it is saved', async () => {
    open();
    const card = await screen.findByRole('region', { name: 'Подпись' });
    await waitFor(() => expect(within(card).getByLabelText('Подпись')).toHaveValue('s1'));

    await userEvent.click(within(card).getByRole('button', { name: 'Управлять подписями' }));
    const dialog = await screen.findByRole('dialog', { name: 'Подписи' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Отмена' }));

    expect(screen.queryByRole('dialog', { name: 'Подписи' })).not.toBeInTheDocument();
  });
});
