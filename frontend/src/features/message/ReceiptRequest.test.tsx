import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { resetCsrfToken } from '@/shared/api/client';
import { ToastProvider } from '@/shared/toast';
import { messageKey, type MessageDetail } from './api';
import { ReceiptRequest } from './ReceiptRequest';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const message = (patch: Partial<MessageDetail> = {}): MessageDetail =>
  ({ id: 'SU5CT1g.1.7', receiptRequested: true, ...patch }) as MessageDetail;

let posts: { url: string; body: unknown }[];
let answer: () => Response;
let client: QueryClient;

beforeEach(() => {
  resetCsrfToken();
  posts = [];
  answer = () => new Response(null, { status: 204 });
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      if (input === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      posts.push({ url: String(input), body: JSON.parse(String(init?.body)) });
      return Promise.resolve(answer());
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function show(detail: MessageDetail = message()) {
  client.setQueryData(messageKey(detail.id), detail);
  render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <ReceiptRequest message={detail} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

describe('ReceiptRequest', () => {
  it('shows nothing for a message that asks for no receipt', () => {
    show(message({ receiptRequested: false }));

    expect(screen.queryByRole('region')).not.toBeInTheDocument();
  });

  it('asks the reader, in words, and sends nothing by itself', () => {
    show();

    const section = screen.getByRole('region', { name: 'Запрос подтверждения прочтения' });
    expect(section).toHaveTextContent('Подтверждение сообщит ему, когда вы открыли письмо');
    expect(screen.getByRole('button', { name: 'Отправить подтверждение' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Не отправлять' })).toBeEnabled();
    expect(posts).toHaveLength(0);
  });

  it('sends the receipt when the reader says so, says it was sent, and does not ask again', async () => {
    show();

    await userEvent.click(screen.getByRole('button', { name: 'Отправить подтверждение' }));

    await waitFor(() =>
      expect(posts).toEqual([
        { url: '/api/v1/messages/SU5CT1g.1.7/receipt', body: { send: true } },
      ]),
    );
    expect(await screen.findByText('Подтверждение отправлено')).toBeInTheDocument();
    expect(client.getQueryData<MessageDetail>(messageKey('SU5CT1g.1.7'))?.receiptRequested).toBe(
      false,
    );
  });

  it('declines without a word to the sender, and does not ask again', async () => {
    show();

    await userEvent.click(screen.getByRole('button', { name: 'Не отправлять' }));

    await waitFor(() =>
      expect(posts).toEqual([
        { url: '/api/v1/messages/SU5CT1g.1.7/receipt', body: { send: false } },
      ]),
    );
    expect(screen.queryByText('Подтверждение отправлено')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(client.getQueryData<MessageDetail>(messageKey('SU5CT1g.1.7'))?.receiptRequested).toBe(
        false,
      ),
    );
  });

  it('keeps the question and says in words why when the mail service refuses', async () => {
    answer = () => json({ code: 'send_refused', title: 'x' }, { status: 422 });
    show();

    await userEvent.click(screen.getByRole('button', { name: 'Отправить подтверждение' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('отказалась');
    expect(client.getQueryData<MessageDetail>(messageKey('SU5CT1g.1.7'))?.receiptRequested).toBe(
      true,
    );
    expect(screen.getByRole('button', { name: 'Отправить подтверждение' })).toBeEnabled(); // it can be tried again
  });
});
