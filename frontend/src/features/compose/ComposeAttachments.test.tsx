import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { resetCsrfToken } from '@/shared/api/client';
import { FakeXhr } from '@/shared/api/fake-xhr';
import { ComposeScreen } from './ComposeScreen';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const LIMITS = { maxFileBytes: 1000, maxTotalBytes: 1500, maxFiles: 3 };

let waiting: { id: string; fileName: string; contentType: string; size: number }[];
let calls: { url: string; method: string }[];
let restore: () => void;

beforeEach(() => {
  resetCsrfToken();
  waiting = [];
  calls = [];
  restore = FakeXhr.install();
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      calls.push({ url: String(input), method });
      if (input === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      if (input === '/api/v1/attachments')
        return Promise.resolve(json({ items: waiting, limits: LIMITS }));
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
});

afterEach(() => restore());

function open() {
  const router = createMemoryRouter(
    [{ path: '/compose', element: <ComposeScreen address="user@example.com" /> }],
    { initialEntries: ['/compose'] },
  );
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

const input = () => screen.getByTestId('attach-input');
const file = (name: string, size: number) =>
  new File([new Uint8Array(size)], name, { type: 'image/png' });
const lastXhr = () => FakeXhr.instances.at(-1)!;

describe('attachments of the new message', () => {
  it('shows a card with the percentage and a progress bar while a file is sent, then its size', async () => {
    open();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/attachments')).toBe(true));

    await userEvent.upload(input(), file('mobile-pricing.png', 640));
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(1));

    const card = screen.getByText('mobile-pricing.png').closest('li')!;
    expect(card).toHaveTextContent('PNG');
    expect(
      screen.getByRole('progressbar', { name: 'Загрузка: mobile-pricing.png' }),
    ).toHaveAttribute('aria-valuenow', '0');

    lastXhr().progress(68, 100);
    await waitFor(() => expect(card).toHaveTextContent('68%'));
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '68');

    lastXhr().answer(201, {
      id: 'a1',
      fileName: 'mobile-pricing.png',
      contentType: 'image/png',
      size: 640,
    });
    await waitFor(() => expect(screen.queryByRole('progressbar')).not.toBeInTheDocument());
    expect(card).toHaveTextContent('640');
  });

  it('cancels an upload on the way: the request is aborted and the card is gone', async () => {
    open();
    await userEvent.upload(input(), file('big.png', 500));
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(1));

    await userEvent.click(screen.getByRole('button', { name: 'Отменить загрузку: big.png' }));

    expect(lastXhr().aborted).toBe(true);
    expect(screen.queryByText('big.png')).not.toBeInTheDocument();
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('takes a finished file off the server and out of the list', async () => {
    open();
    await userEvent.upload(input(), file('a.png', 10));
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    lastXhr().answer(201, { id: 'a1', fileName: 'a.png', contentType: 'image/png', size: 10 });
    const remove = await screen.findByRole('button', { name: 'Убрать файл: a.png' });

    await userEvent.click(remove);

    expect(screen.queryByText('a.png')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(calls).toContainEqual({ url: '/api/v1/attachments/a1', method: 'DELETE' }),
    );
  });

  it('refuses a file over the limit in words, without sending it, and keeps it in the list to be taken off', async () => {
    open();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/attachments')).toBe(true));
    await screen.findByLabelText('Прикрепить файл');

    await userEvent.upload(input(), file('huge.png', 1001));

    expect(await screen.findByRole('alert')).toHaveTextContent('слишком большой');
    expect(FakeXhr.instances).toHaveLength(0);
    await userEvent.click(screen.getByRole('button', { name: 'Убрать файл: huge.png' }));
    expect(screen.queryByText('huge.png')).not.toBeInTheDocument();
  });

  it('refuses the file that would make the files too big together, and one too many', async () => {
    open();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/attachments')).toBe(true));
    await screen.findByLabelText('Прикрепить файл');

    await userEvent.upload(input(), [file('a.png', 900), file('b.png', 700)]);
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    expect(await screen.findByRole('alert')).toHaveTextContent('вместе слишком большие');

    await userEvent.upload(input(), [file('c.png', 10), file('d.png', 10), file('e.png', 10)]);
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(3));
    expect(screen.getAllByRole('alert').at(-1)).toHaveTextContent('слишком много файлов');
  });

  it('says why in words when the server refuses a file, and when the network fails', async () => {
    open();
    await userEvent.upload(input(), file('a.png', 10));
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(1));

    lastXhr().answer(413, { code: 'attachment_too_large', title: 'x' });
    expect(await screen.findByRole('alert')).toHaveTextContent('слишком большой');

    await userEvent.upload(input(), file('b.png', 10));
    await waitFor(() => expect(FakeXhr.instances).toHaveLength(2));
    lastXhr().fail();
    await waitFor(() => expect(screen.getAllByRole('alert')).toHaveLength(2));
    expect(screen.getAllByRole('alert')[1]).toHaveTextContent(/связ/i);
  });

  it('shows the files that already wait on the server, and counts them against the limits', async () => {
    waiting = [
      { id: 's1', fileName: 'old.pdf', contentType: 'application/pdf', size: 1000 },
      { id: 's2', fileName: 'older.pdf', contentType: 'application/pdf', size: 400 },
    ];
    open();

    expect(await screen.findByText('old.pdf')).toBeInTheDocument();
    await userEvent.upload(input(), file('c.png', 200));

    expect(await screen.findByRole('alert')).toHaveTextContent('вместе слишком большие');
    expect(FakeXhr.instances).toHaveLength(0);
  });
});
