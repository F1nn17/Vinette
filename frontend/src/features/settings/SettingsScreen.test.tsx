import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import i18n from '@/shared/i18n';
import { LanguageSync } from './LanguageSync';
import { SettingsScreen } from './SettingsScreen';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

interface State {
  settings: {
    density: string;
    language: string | null;
    labelColors: Record<string, string>;
    trustedImageSenders: string[];
    signatures: unknown[];
  };
  patches: Record<string, unknown>[];
  fail?: boolean;
}

function mockApi(overrides: Partial<State['settings']> = {}): State {
  const state: State = {
    settings: {
      density: 'comfortable',
      language: null,
      labelColors: {},
      trustedImageSenders: ['anna@example.org', 'bob@example.org'],
      signatures: [],
      ...overrides,
    },
    patches: [],
  };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = String(input);
      if (url === '/api/v1/csrf') return Promise.resolve(json({ token: 't' }));
      if (url === '/api/v1/settings') {
        if (init?.method === 'PATCH') {
          const body = JSON.parse(String(init.body)) as Record<string, string | null>;
          state.patches.push(body);
          if (state.fail)
            return Promise.resolve(json({ code: 'internal_error', title: 'x' }, { status: 500 }));
          if (body.density) state.settings.density = body.density;
          if (body.language !== null && body.language !== undefined)
            state.settings.language = body.language || null;
          if (body.untrustImagesFrom)
            state.settings.trustedImageSenders = state.settings.trustedImageSenders.filter(
              (a) => a !== body.untrustImagesFrom,
            );
        }
        return Promise.resolve(json(state.settings));
      }
      return Promise.resolve(json({ code: 'not_found' }, { status: 404 }));
    }),
  );
  return state;
}

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <LanguageSync />
      <SettingsScreen />
    </QueryClientProvider>,
  );
}

afterEach(async () => {
  localStorage.clear();
  vi.unstubAllGlobals();
  await i18n.changeLanguage('ru');
});

describe('SettingsScreen', () => {
  it('shows what is chosen now (and speaks the chosen language)', async () => {
    mockApi({ density: 'compact', language: 'en' });
    show();

    expect(await screen.findByRole('button', { name: 'Compact' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: 'Comfortable' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
    expect(screen.getByRole('combobox', { name: 'Language' })).toHaveValue('en');
    expect(screen.getByRole('heading', { level: 2, name: 'Signatures' })).toBeInTheDocument();
  });

  it('saves the density at once and shows it at once', async () => {
    const state = mockApi();
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByRole('button', { name: 'Компактная' }));

    expect(screen.getByRole('button', { name: 'Компактная' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await waitFor(() =>
      expect(state.patches).toEqual([{ density: 'compact', language: null, labelColors: null }]),
    );
  });

  it('changes the language at once, saves it, and "same as the browser" saves an empty one', async () => {
    const state = mockApi();
    const user = userEvent.setup();
    show();

    await user.selectOptions(await screen.findByRole('combobox', { name: 'Язык' }), 'en');

    expect(i18n.language).toBe('en');
    await waitFor(() =>
      expect(state.patches.at(-1)).toMatchObject({ language: 'en', density: null }),
    );
    expect(await screen.findByRole('combobox', { name: 'Language' })).toHaveValue('en');

    await user.selectOptions(screen.getByRole('combobox', { name: 'Language' }), '');

    await waitFor(() => expect(state.patches.at(-1)).toMatchObject({ language: '' }));
  });

  it('puts the choice back and says so when the server refuses', async () => {
    const state = mockApi();
    state.fail = true;
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByRole('button', { name: 'Компактная' }));

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Обычная' })).toHaveAttribute(
        'aria-pressed',
        'true',
      ),
    );
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('lists the senders whose pictures are shown and takes one off', async () => {
    const state = mockApi();
    const user = userEvent.setup();
    show();

    const list = await screen.findByRole('list', { name: 'Картинки в письмах' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);

    await user.click(
      within(list).getByRole('button', {
        name: 'Больше не показывать картинки от anna@example.org',
      }),
    );

    await waitFor(() =>
      expect(state.patches.at(-1)).toMatchObject({ untrustImagesFrom: 'anna@example.org' }),
    );
    await waitFor(() =>
      expect(
        within(screen.getByRole('list', { name: 'Картинки в письмах' })).getAllByRole('listitem'),
      ).toHaveLength(1),
    );
  });

  it('says so when no sender is trusted', async () => {
    mockApi({ trustedImageSenders: [] });
    show();

    expect(await screen.findByText(/Пока таких отправителей нет/)).toBeInTheDocument();
  });
});
