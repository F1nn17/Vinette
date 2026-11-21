import {
  applyDocumentLanguage,
  initialLanguage,
  languageFor,
  rememberedLanguage,
  rememberLanguage,
} from './language';

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe('languageFor', () => {
  it('takes the chosen language, else the browser language if the application has it, else Russian', () => {
    expect(languageFor('en', 'ru-RU')).toBe('en');
    expect(languageFor(null, 'en-GB')).toBe('en');
    expect(languageFor('', 'EN-us')).toBe('en');
    expect(languageFor(undefined, 'ru')).toBe('ru');
    expect(languageFor(null, 'de-DE')).toBe('ru');
    expect(languageFor('fr', 'ru')).toBe('ru');
    expect(languageFor(null, '')).toBe('ru');
  });
});

describe('the remembered language', () => {
  it('is kept for an explicit choice and forgotten for "same as the browser"', () => {
    expect(rememberedLanguage()).toBeNull();

    rememberLanguage('en');
    expect(rememberedLanguage()).toBe('en');

    rememberLanguage('');
    expect(rememberedLanguage()).toBeNull();
    rememberLanguage('en');
    rememberLanguage(null);
    expect(rememberedLanguage()).toBeNull();
  });

  it('ignores a language the application does not have', () => {
    localStorage.setItem('vinette.language', 'fr');

    expect(rememberedLanguage()).toBeNull();
    rememberLanguage('fr');
    expect(localStorage.getItem('vinette.language')).toBeNull();
  });

  it('is the start language, else the browser one; a browser that keeps nothing is no problem', () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('en-US');
    expect(initialLanguage()).toBe('en');

    rememberLanguage('ru');
    expect(initialLanguage()).toBe('ru');

    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(initialLanguage()).toBe('en');
    expect(() => rememberLanguage('ru')).not.toThrow();
  });
});

describe('applyDocumentLanguage', () => {
  it('tells the page which language it is in', () => {
    applyDocumentLanguage('en');
    expect(document.documentElement.lang).toBe('en');
    applyDocumentLanguage('ru');
    expect(document.documentElement.lang).toBe('ru');
  });
});
