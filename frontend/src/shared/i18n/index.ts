import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './en.json';
import { applyDocumentLanguage, DEFAULT_LANGUAGE, initialLanguage } from './language';
import ru from './ru.json';

void i18n.use(initReactI18next).init({
  resources: { ru: { translation: ru }, en: { translation: en } },
  // The remembered choice, else the browser's language; the settings correct it once they are read after sign-in (`LanguageSync`).
  lng: initialLanguage(),
  fallbackLng: DEFAULT_LANGUAGE,
  interpolation: { escapeValue: false },
});

applyDocumentLanguage(i18n.language);
i18n.on('languageChanged', applyDocumentLanguage);

export default i18n;
