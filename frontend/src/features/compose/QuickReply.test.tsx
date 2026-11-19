import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { resetCsrfToken } from '@/shared/api/client';
import { ToastProvider } from '@/shared/toast';
import { QuickReply } from './QuickReply';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const QUOTE = {
  messageId: 'm3@example.org',
  references: ['m1@example.org'],
  from: { name: 'Анна Смирнова', address: 'anna@example.org' },
  replyTo: [],
  to: [{ name: '', address: 'me@vinette.test' }],
  cc: [],
  date: '2026-11-17T09:00:00Z',
  subject: 'Смета',
  text: 'Привет',
  html: null,
  attachments: [],
};

interface Call {
  url: string;
  method: string;
  body?: Record<string, unknown>;
}

let calls: Call[];
let quoteAnswer: () => Response;
let sendAnswer: () => Response;
let signatures: { id: string; name: string; text: string; default: boolean }[];

beforeEach(() => {
  resetCsrfToken();
  calls = [];
  quoteAnswer = () => json(QUOTE);
  sendAnswer = () => json({ messageId: 'x', copy: 'saved', copyId: 'c', draftRemoved: true });
  signatures = [];
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
      if (url === '/api/v1/settings')
        return Promise.resolve(
          json({
            density: 'comfortable',
            language: null,
            labelColors: {},
            trustedImageSenders: [],
            signatures,
          }),
        );
      if (url === '/api/v1/messages') return Promise.resolve(sendAnswer());
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function open() {
  const router = createMemoryRouter(
    [
      {
        path: '/mail/inbox/:id',
        element: (
          <QuickReply
            messageId="SU5CT1g.1.7"
            senderName="Анна Смирнова"
            address="me@vinette.test"
          />
        ),
      },
      { path: '/compose', element: <p>compose-screen</p> },
    ],
    { initialEntries: ['/mail/inbox/SU5CT1g.1.7'] },
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

const field = () => screen.getByLabelText('Быстрый ответ');
const sends = () => calls.filter((c) => c.url === '/api/v1/messages');

describe('quick reply', () => {
  it('names the sender in the field, and cannot send while it is empty', () => {
    open();

    expect(field()).toHaveAttribute('placeholder', 'Ответить: Анна Смирнова');
    expect(screen.getByRole('button', { name: 'Отправить' })).toBeDisabled();
    expect(calls.some((c) => c.url.includes('/quote'))).toBe(false); // the message is not read before an answer is sent
  });

  it('sends the answer to the sender, into the conversation, with the original quoted', async () => {
    open();
    await userEvent.type(field(), 'Спасибо, <b>беру</b>');

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(sends()).toHaveLength(1));
    const body = sends()[0]!.body!;
    expect(body).toMatchObject({
      to: [{ name: 'Анна Смирнова', address: 'anna@example.org' }],
      cc: [],
      bcc: [],
      subject: 'Re: Смета',
      inReplyTo: 'm3@example.org',
      references: ['m1@example.org', 'm3@example.org'],
      attachmentIds: [],
      draftId: null,
    });
    const html = String(body.html);
    expect(html.startsWith('<p>Спасибо, &lt;b&gt;беру&lt;/b&gt;</p>')).toBe(true); // typed text is never markup
    expect(html).toContain('<blockquote><p>Привет</p></blockquote>');
    expect(calls.find((c) => c.url.includes('/quote'))!.url).toBe(
      '/api/v1/messages/SU5CT1g.1.7/quote',
    );
    expect(await screen.findByText('Письмо отправлено')).toBeInTheDocument();
    await waitFor(() => expect(field()).toHaveValue(''));
  });

  it('sends with Ctrl+Enter', async () => {
    open();
    await userEvent.type(field(), 'Ок{Control>}{Enter}{/Control}');

    await waitFor(() => expect(sends()).toHaveLength(1));
  });

  it('sends only once however often it is pressed', async () => {
    open();
    await userEvent.type(field(), 'Ок');

    await userEvent.dblClick(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(sends().length).toBeGreaterThan(0));
    expect(sends()).toHaveLength(1);
  });

  it('keeps the text and says in words why when the server refuses', async () => {
    sendAnswer = () =>
      json(
        { code: 'recipient_rejected', title: 'x', address: 'anna@example.org' },
        { status: 422 },
      );
    open();
    await userEvent.type(field(), 'Ок');

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('anna@example.org');
    expect(field()).toHaveValue('Ок');
    expect(screen.getByRole('button', { name: 'Отправить' })).toBeEnabled();
  });

  it('keeps the text and says so when it cannot tell who to reply to', async () => {
    quoteAnswer = () => json({ ...QUOTE, from: null, replyTo: [] });
    open();
    await userEvent.type(field(), 'Ок');

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('кому ответить');
    expect(sends()).toHaveLength(0);
    expect(field()).toHaveValue('Ок');
  });

  it('opens the editor on the same message with what was typed', async () => {
    const router = open();
    await userEvent.type(field(), 'Начало ответа');

    await userEvent.click(screen.getByRole('button', { name: 'Открыть в редакторе' }));

    expect(router.state.location.pathname).toBe('/compose');
    expect(router.state.location.search).toBe('?reply=SU5CT1g.1.7');
    expect(router.state.location.state).toEqual({ typed: 'Начало ответа' });
  });

  it('puts the default signature under the answer, once, as escaped text', async () => {
    signatures = [
      { id: 's1', name: 'Work', text: 'Regards,<br>Alice', default: true },
      { id: 's2', name: 'Home', text: 'Cheers', default: false },
    ];
    open();
    await userEvent.type(field(), 'Ок');
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/settings')).toBe(true));
    await new Promise((r) => setTimeout(r, 50)); // the settings are in

    await userEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(sends()).toHaveLength(1));
    const html = String(sends()[0]!.body!.html);
    expect(html.endsWith('<p></p><p>Regards,&lt;br&gt;Alice</p>')).toBe(true);
    expect(html.split('Regards').length).toBe(2);
    expect(html).not.toContain('Cheers');
  });
});
