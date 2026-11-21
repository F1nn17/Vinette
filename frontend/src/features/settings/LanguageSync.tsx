import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { languageFor, rememberLanguage } from '@/shared/i18n/language';
import { useSettings } from './api';

/**
 * Makes the interface speak the language from the settings (VIN-113): once they are read after sign-in, and again at once when the choice is
 * changed on the settings screen (the cache is changed before the server has answered). The choice is remembered in the browser, so the next
 * start (the sign-in screen included) already speaks it. Shows nothing.
 */
export function LanguageSync() {
  const { i18n } = useTranslation();
  const { data } = useSettings();
  const choice = data?.language;

  useEffect(() => {
    if (data === undefined) return;
    rememberLanguage(choice);
    const language = languageFor(choice);
    if (i18n.language !== language) void i18n.changeLanguage(language);
    // `data` only says that the settings are known; the choice is what matters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [choice, data === undefined, i18n]);

  return null;
}
