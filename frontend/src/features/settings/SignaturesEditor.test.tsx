import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { resetCsrfToken } from '@/shared/api/client';
import { SignaturesEditor } from './SignaturesEditor';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

type Sig = { id: string; name: string; text: string; default: boolean };

let stored: Sig[];
let patches: Record<string, unknown>[];
let refuse: boolean;

const settings = () => ({
  density: 'comfortable',
  language: null,
  labelColors: {},
  trustedImageSenders: [],
  signatures: stored,
});

beforeEach(() => {
  resetCsrfToken();
  stored = [
    { id: 's1', name: 'Work', text: 'Regards,\nAlice', default: true },
    { id: 's2', name: 'Home', text: 'Cheers', default: false },
  ];
  patches = [];
  refuse = false;
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      if (input === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      if (input === '/api/v1/settings' && init?.method === 'PATCH') {
        patches.push(JSON.parse(String(init.body)) as Record<string, unknown>);
        if (refuse)
          return Promise.resolve(json({ code: 'bad_request', title: 'x' }, { status: 400 }));
        const sent = (
          patches.at(-1)!.signatures as {
            id: string | null;
            name: string;
            text: string;
            default: boolean;
          }[]
        ).map((s, i) => ({ ...s, id: s.id ?? `new${i}` }));
        stored = sent;
        return Promise.resolve(json(settings()));
      }
      if (input === '/api/v1/settings') return Promise.resolve(json(settings()));
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function open(props: { onSaved?: () => void; onCancel?: () => void } = {}) {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <SignaturesEditor {...props} />
    </QueryClientProvider>,
  );
}

const names = () =>
  within(screen.getByRole('list', { name: 'Список подписей' }))
    .getAllByRole('button')
    .map((b) => b.textContent);

describe('SignaturesEditor', () => {
  it('lists the signatures, marks the default, and shows the chosen one with a preview', async () => {
    open();

    expect(await screen.findByRole('list', { name: 'Список подписей' })).toBeInTheDocument();
    expect(names()).toEqual(['Work (по умолчанию)', 'Home']);
    expect(screen.getByLabelText('Название')).toHaveValue('Work');
    expect(screen.getByLabelText('Текст подписи')).toHaveValue('Regards,\nAlice');
    expect(screen.getByRole('group', { name: 'Как это будет выглядеть' })).toHaveTextContent(
      'Regards, Alice',
    );
    expect(screen.getByRole('checkbox', { name: 'Подставлять в новые письма' })).toBeChecked();
  });

  it('says there are none, and makes the first one the default', async () => {
    stored = [];
    open();
    expect(await screen.findByText('Подписей пока нет. Создайте первую.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Новая подпись' }));
    await userEvent.type(screen.getByLabelText('Название'), 'First');
    await userEvent.type(screen.getByLabelText('Текст подписи'), 'Bye');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]).toMatchObject({
      signatures: [{ id: null, name: 'First', text: 'Bye', default: true }],
    });
  });

  it('keeps the ids of the signatures that stay, gives none to a new one, and makes only one the default', async () => {
    const saved = vi.fn();
    open({ onSaved: saved });
    await screen.findByRole('list', { name: 'Список подписей' });

    await userEvent.click(screen.getByRole('button', { name: 'Home' }));
    await userEvent.clear(screen.getByLabelText('Название'));
    await userEvent.type(screen.getByLabelText('Название'), 'House');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Подставлять в новые письма' }));
    await userEvent.click(screen.getByRole('button', { name: 'Новая подпись' }));
    await userEvent.type(screen.getByLabelText('Название'), 'Trip');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(patches[0]!.signatures).toEqual([
      { id: 's1', name: 'Work', text: 'Regards,\nAlice', default: false },
      { id: 's2', name: 'House', text: 'Cheers', default: true },
      { id: null, name: 'Trip', text: '', default: false },
    ]);
  });

  it('deletes a signature from the list, and the rest are sent', async () => {
    open();
    await screen.findByRole('list', { name: 'Список подписей' });

    await userEvent.click(screen.getByRole('button', { name: 'Удалить подпись' }));
    expect(names()).toEqual(['Home']);
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect((patches[0]!.signatures as { id: string }[]).map((s) => s.id)).toEqual(['s2']);
  });

  it('cannot be saved while a signature has no name, and says why', async () => {
    open();
    await screen.findByRole('list', { name: 'Список подписей' });

    await userEvent.clear(screen.getByLabelText('Название'));

    expect(screen.getByRole('button', { name: 'Сохранить' })).toBeDisabled();
    expect(screen.getByText('У каждой подписи должно быть название.')).toBeInTheDocument();
    expect(names()[0]).toBe('Без названия (по умолчанию)');
  });

  it('keeps what was typed and says in words when the server refuses', async () => {
    refuse = true;
    const saved = vi.fn();
    open({ onSaved: saved });
    await screen.findByRole('list', { name: 'Список подписей' });
    await userEvent.type(screen.getByLabelText('Текст подписи'), ' more');

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(saved).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Текст подписи')).toHaveValue('Regards,\nAlice more');
  });

  it('gives up without saving anything', async () => {
    const cancel = vi.fn();
    open({ onCancel: cancel });
    await screen.findByRole('list', { name: 'Список подписей' });

    await userEvent.click(screen.getByRole('button', { name: 'Отмена' }));

    expect(cancel).toHaveBeenCalled();
    expect(patches).toHaveLength(0);
  });
});
