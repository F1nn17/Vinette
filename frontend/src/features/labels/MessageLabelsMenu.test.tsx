import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ToastProvider } from '@/shared/toast';
import { MessageLabelsMenu } from './MessageLabelsMenu';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

interface Patch {
  id: string;
  body: { add: string[] | null; remove: string[] | null };
}

function mockApi(options: { fail?: boolean } = {}) {
  const patches: Patch[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = new URL(String(input), 'http://localhost');
      if (url.pathname === '/api/v1/csrf') return Promise.resolve(json({ token: 'csrf' }));
      if (url.pathname === '/api/v1/labels')
        return Promise.resolve(
          json({
            labels: [
              { name: 'Work', color: null },
              { name: 'Счета', color: null },
            ],
          }),
        );
      const match = /^\/api\/v1\/messages\/(.+)\/labels$/u.exec(url.pathname);
      if (match && init?.method === 'PATCH') {
        patches.push({ id: match[1] ?? '', body: JSON.parse(String(init.body)) as Patch['body'] });
        return Promise.resolve(
          options.fail
            ? json({ code: 'mail_unavailable', title: 'x' }, { status: 503 })
            : json({ labels: [] }),
        );
      }
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
  return patches;
}

function renderMenu(ids: string[], labelsOfMessages: string[][]) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MessageLabelsMenu
          ids={ids}
          labelsOfMessages={labelsOfMessages}
          onClose={() => undefined}
        />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('MessageLabelsMenu', () => {
  it('shows what the open message has, and puts another label on it', async () => {
    const user = userEvent.setup();
    const patches = mockApi();
    renderMenu(['m1'], [['Work']]);

    expect(await screen.findByRole('checkbox', { name: 'Work' })).toBeChecked();
    await user.click(screen.getByRole('checkbox', { name: 'Счета' }));

    await waitFor(() =>
      expect(patches).toEqual([{ id: 'm1', body: { add: ['Счета'], remove: null } }]),
    );
  });

  it('takes a label off', async () => {
    const user = userEvent.setup();
    const patches = mockApi();
    renderMenu(['m1'], [['Work']]);

    await user.click(await screen.findByRole('checkbox', { name: 'Work' }));

    await waitFor(() =>
      expect(patches).toEqual([{ id: 'm1', body: { add: null, remove: ['Work'] } }]),
    );
  });

  it('puts a label on every selected message, one after another, when some had it already', async () => {
    const user = userEvent.setup();
    const patches = mockApi();
    renderMenu(['a', 'b', 'c'], [['Work'], [], []]);

    const box = await screen.findByRole<HTMLInputElement>('checkbox', { name: 'Work' });
    expect(box.indeterminate).toBe(true);
    await user.click(box);

    await waitFor(() => expect(patches.map((p) => p.id)).toEqual(['a', 'b', 'c']));
    expect(patches.every((p) => p.body.add?.[0] === 'Work' && p.body.remove === null)).toBe(true);
  });

  it('makes a new label from a typed name and puts it on the messages', async () => {
    const user = userEvent.setup();
    const patches = mockApi();
    renderMenu(['m1'], [[]]);

    await screen.findByRole('checkbox', { name: 'Work' });
    await user.type(screen.getByRole('textbox', { name: 'Название новой метки' }), 'Отпуск{Enter}');

    await waitFor(() =>
      expect(patches).toEqual([{ id: 'm1', body: { add: ['Отпуск'], remove: null } }]),
    );
  });

  it('uses the label that exists when a name differs only in capitals', async () => {
    const user = userEvent.setup();
    const patches = mockApi();
    renderMenu(['m1'], [[]]);

    await screen.findByRole('checkbox', { name: 'Work' });
    await user.type(screen.getByRole('textbox', { name: 'Название новой метки' }), 'WORK{Enter}');

    await waitFor(() => expect(patches[0]?.body.add).toEqual(['Work']));
  });

  it('puts the old state back and says so when the server refuses', async () => {
    const user = userEvent.setup();
    mockApi({ fail: true });
    renderMenu(['m1'], [[]]);

    await user.click(await screen.findByRole('checkbox', { name: 'Work' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Почта временно недоступна');
  });
});
