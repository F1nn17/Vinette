import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, waitFor } from '@testing-library/react';
import i18n from '@/shared/i18n';
import { rememberedLanguage } from '@/shared/i18n/language';
import { SETTINGS_KEY } from './api';
import { LanguageSync } from './LanguageSync';

const json = (body: unknown) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });

function show(language: string | null) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(() =>
      Promise.resolve(
        json({
          density: 'comfortable',
          language,
          labelColors: {},
          trustedImageSenders: [],
          signatures: [],
        }),
      ),
    ),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <LanguageSync />
    </QueryClientProvider>,
  );
  return client;
}

afterEach(async () => {
  // unmounted first: a screen that is still there would answer the language that is put back below
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
  await i18n.changeLanguage('ru');
});

describe('LanguageSync', () => {
  it('speaks the language of the settings once they are read, and remembers it for the next start', async () => {
    show('en');

    await waitFor(() => expect(i18n.language).toBe('en'));
    expect(rememberedLanguage()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });

  it('follows the browser when no language is chosen, and forgets a remembered choice', async () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('en-GB');
    localStorage.setItem('vinette.language', 'ru');
    show(null);

    await waitFor(() => expect(i18n.language).toBe('en'));
    expect(rememberedLanguage()).toBeNull();
  });

  it('changes at once when the choice in the settings is changed, before the server has answered', async () => {
    const client = show('ru');
    await waitFor(() => expect(client.getQueryData(SETTINGS_KEY)).toBeDefined());
    expect(i18n.language).toBe('ru');

    client.setQueryData(SETTINGS_KEY, {
      ...(client.getQueryData(SETTINGS_KEY) as object),
      language: 'en',
    });

    await waitFor(() => expect(i18n.language).toBe('en'));
    expect(rememberedLanguage()).toBe('en');
  });

  it('does nothing before the settings are known', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(() => new Promise<Response>(() => undefined)),
    );
    localStorage.setItem('vinette.language', 'en');
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <LanguageSync />
      </QueryClientProvider>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(i18n.language).toBe('ru');
    expect(rememberedLanguage()).toBe('en');
  });
});
