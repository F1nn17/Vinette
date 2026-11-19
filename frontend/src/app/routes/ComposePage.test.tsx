import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { SessionContext, type SessionValue } from '@/features/auth/session-context';
import { resetCsrfToken } from '@/shared/api/client';
import { ToastProvider } from '@/shared/toast';
import { ComposePage } from './ComposePage';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const session: SessionValue = {
  status: 'signedIn',
  address: 'me@vinette.test',
  notice: null,
  signIn: () => Promise.resolve(),
  signOut: () => Promise.resolve(),
};

const QUOTE = {
  messageId: 'm3@example.org',
  references: ['m1@example.org', 'm2@example.org'],
  from: { name: 'Анна Смирнова', address: 'anna@example.org' },
  replyTo: [],
  to: [
    { name: '', address: 'me@vinette.test' },
    { name: 'Boris', address: 'boris@example.org' },
  ],
  cc: [{ name: '', address: 'carol@example.org' }],
  date: '2026-11-17T09:00:00Z',
  subject: 'Смета',
  text: 'Привет, смотри файл.',
  html: null,
  attachments: [] as unknown[],
};

interface Call {
  url: string;
  method: string;
  body?: Record<string, unknown>;
}

let calls: Call[];
let quoteAnswer: () => Response;
let waiting: unknown[];

beforeEach(() => {
  resetCsrfToken();
  calls = [];
  waiting = [];
  quoteAnswer = () => json(QUOTE);
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const url = String(input);
      calls.push({
        url,
        method: init?.method ?? 'GET',
        body:
          typeof init?.body === 'string'
            ? (JSON.parse(init.body) as Record<string, unknown>)
            : undefined,
      });
      if (url === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      if (url.includes('/quote')) return Promise.resolve(quoteAnswer());
      if (url === '/api/v1/attachments')
        return Promise.resolve(
          json({
            items: waiting,
            limits: { maxFileBytes: 1e6, maxTotalBytes: 1e6, maxFiles: 5 },
          }),
        );
      if (url === '/api/v1/drafts')
        return Promise.resolve(
          json({
            id: 'd1',
            messageId: 'dm',
            savedAt: '2026-11-18T14:41:00Z',
            previousRemoved: true,
          }),
        );
      if (url === '/api/v1/messages')
        return Promise.resolve(
          json({ messageId: 'x', copy: 'saved', copyId: 'c', draftRemoved: true }),
        );
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function open(search: string, state?: unknown) {
  const router = createMemoryRouter(
    [
      { path: '/compose', element: <ComposePage /> },
      { path: '/mail/inbox', element: <p>inbox-screen</p> },
    ],
    { initialEntries: [{ pathname: '/compose', search, state }] },
  );
  render(
    <SessionContext.Provider value={session}>
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
      >
        <ToastProvider>
          <RouterProvider router={router} />
        </ToastProvider>
      </QueryClientProvider>
    </SessionContext.Provider>,
  );
  return router;
}

const to = () => screen.getByRole('list', { name: 'Получатели: Кому' });

describe('answering and forwarding', () => {
  it('opens an empty message for /compose', async () => {
    open('');

    expect(await screen.findByLabelText('Тема')).toHaveValue('');
    expect(calls.some((c) => c.url.includes('/quote'))).toBe(false);
  });

  it('starts a reply from the message: the sender, "Re:", the original quoted, the cursor above it', async () => {
    open('?reply=SU5CT1g.1.7');

    expect(await screen.findByLabelText('Тема')).toHaveValue('Re: Смета');
    expect(to()).toHaveTextContent('Анна Смирнова');
    expect(screen.queryByRole('list', { name: 'Получатели: Копия' })).not.toBeInTheDocument();
    const editor = await screen.findByRole('textbox', { name: 'Текст письма' });
    expect(editor).toHaveTextContent('Привет, смотри файл.');
    expect(editor.querySelector('blockquote')).not.toBeNull();
    expect(calls.find((c) => c.url.includes('/quote'))!.url).toBe(
      '/api/v1/messages/SU5CT1g.1.7/quote',
    );
  });

  it('puts what was typed in the quick reply above the quote', async () => {
    open('?reply=SU5CT1g.1.7', { typed: 'Начало <b>ответа</b>' });

    const editor = await screen.findByRole('textbox', { name: 'Текст письма' });
    expect(editor).toHaveTextContent('Начало <b>ответа</b>'); // text, not markup
    expect(editor.querySelector('b')).toBeNull();
    expect(editor.firstElementChild).toHaveTextContent('Начало <b>ответа</b>');
    expect(editor.querySelector('blockquote')).toHaveTextContent('Привет, смотри файл.');
  });

  it('starts a reply to everybody with the other recipients in "Cc", and sends it into the conversation', async () => {
    const router = open('?replyAll=SU5CT1g.1.7');
    await screen.findByLabelText('Тема');
    expect(to()).toHaveTextContent('Анна Смирнова');
    expect(screen.getByRole('list', { name: 'Получатели: Копия' })).toHaveTextContent('Boris');
    expect(screen.getByRole('list', { name: 'Получатели: Копия' })).toHaveTextContent(
      'carol@example.org',
    );

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/mail/inbox'));
    const sent = calls.find((c) => c.url === '/api/v1/messages')!.body!;
    expect(sent).toMatchObject({
      subject: 'Re: Смета',
      to: [{ address: 'anna@example.org' }],
      inReplyTo: 'm3@example.org',
      references: ['m1@example.org', 'm2@example.org', 'm3@example.org'],
    });
    expect((sent.cc as unknown[]).length).toBe(2);
    expect(String(sent.html)).toContain('<blockquote>');
  });

  it('starts a forward without recipients, with "Fwd:", the headers of the original and its files', async () => {
    waiting = [{ id: 'f1', fileName: 'report.pdf', contentType: 'application/pdf', size: 5 }];
    quoteAnswer = () => json({ ...QUOTE, attachments: waiting });
    open('?forward=SU5CT1g.1.7');

    expect(await screen.findByLabelText('Тема')).toHaveValue('Fwd: Смета');
    expect(to()).toBeEmptyDOMElement();
    const editor = await screen.findByRole('textbox', { name: 'Текст письма' });
    expect(editor).toHaveTextContent('Пересылаемое письмо');
    expect(editor).toHaveTextContent('Анна Смирнова');
    expect(editor.querySelector('blockquote')).toBeNull();
    expect(calls.find((c) => c.url.includes('/quote'))!.url).toBe(
      '/api/v1/messages/SU5CT1g.1.7/quote?attachments=true',
    );
    expect(
      within(await screen.findByRole('list', { name: 'Прикреплённые файлы' })).getByText(
        'report.pdf',
      ),
    ).toBeInTheDocument();
  });

  it('says in words when the message cannot be opened, and leaves a way back', async () => {
    quoteAnswer = () => json({ code: 'not_found', title: 'x' }, { status: 404 });
    open('?reply=SU5CT1g.1.7');

    expect(await screen.findByRole('alert')).toHaveTextContent('уже нет');
    expect(screen.getByRole('link', { name: 'Входящие' })).toHaveAttribute('href', '/mail/inbox');
  });
});
