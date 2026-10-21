import { Eye, EyeOff } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '@/shared/api/client';
import { getBranding } from '@/shared/lib/branding';
import { Button, Checkbox, IconButton, Logo, TextField } from '@/shared/ui';
import { useSession } from './session-context';

/** Maps an error of the sign-in call to a text for the user (no technical wording, see docs/design/DESIGN.md). */
function errorMessage(
  error: unknown,
  t: (key: string, options?: Record<string, unknown>) => string,
) {
  if (error instanceof ApiError) {
    if (error.code === 'too_many_requests') {
      const minutes = Math.max(1, Math.ceil((error.retryAfterSeconds ?? 60) / 60));
      return t('login.errors.too_many_requests', { minutes });
    }
    if (
      error.code === 'invalid_credentials' ||
      error.code === 'mail_unavailable' ||
      error.code === 'validation_failed'
    ) {
      return t(`login.errors.${error.code}`);
    }
  }
  // fetch rejects with a TypeError when the browser cannot reach the application at all.
  if (error instanceof TypeError) return t('login.errors.network');
  return t('login.errors.unknown');
}

function LoginForm() {
  const { t } = useTranslation();
  const { signIn } = useSession();
  const [address, setAddress] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<{ address?: string; password?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (submitting) return;

    const errors = {
      address: address.trim() ? undefined : t('login.errors.addressRequired'),
      password: password ? undefined : t('login.errors.passwordRequired'),
    };
    setFieldErrors(errors);
    setFormError(null);
    if (errors.address || errors.password) return;

    setSubmitting(true);
    try {
      await signIn(address.trim(), password, rememberMe);
      // The session provider now knows the user and the route guard leaves this screen.
    } catch (error) {
      setFormError(errorMessage(error, t));
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={(event) => void onSubmit(event)} noValidate className="flex flex-col gap-5">
      <TextField
        label={t('login.address')}
        type="email"
        name="username"
        autoComplete="username"
        autoCapitalize="none"
        spellCheck={false}
        inputMode="email"
        autoFocus
        placeholder={t('login.addressPlaceholder')}
        value={address}
        onChange={(event) => setAddress(event.target.value)}
        error={fieldErrors.address}
      />
      <TextField
        label={t('login.password')}
        type={showPassword ? 'text' : 'password'}
        name="password"
        autoComplete="current-password"
        value={password}
        onChange={(event) => setPassword(event.target.value)}
        error={fieldErrors.password}
        endSlot={
          <IconButton
            aria-label={showPassword ? t('login.hidePassword') : t('login.showPassword')}
            onClick={() => setShowPassword((visible) => !visible)}
          >
            {showPassword ? (
              <EyeOff size={18} strokeWidth={1.8} />
            ) : (
              <Eye size={18} strokeWidth={1.8} />
            )}
          </IconButton>
        }
      />
      <Checkbox
        label={t('login.remember')}
        checked={rememberMe}
        onChange={(event) => setRememberMe(event.target.checked)}
      />

      {formError && (
        <p
          role="alert"
          className="rounded-sm bg-warning-bg px-3 py-2.5 text-secondary text-warning"
        >
          {formError}
        </p>
      )}

      <Button type="submit" size="xl" fullWidth disabled={submitting} aria-busy={submitting}>
        {submitting ? t('login.submitting') : t('login.submit')}
      </Button>

      <p className="text-meta text-muted">{t('login.passwordNote')}</p>
    </form>
  );
}

/**
 * The sign-in screen (docs/design/DESIGN.md section 7): a dark brand panel and the form card. The panel is left out on
 * narrow screens, where the logo moves above the card. The "sign in with a work account" option is not shown: no
 * single sign-on is configured.
 */
export function LoginScreen() {
  const { t } = useTranslation();
  const { name, tagline } = getBranding();

  return (
    <div className="grid min-h-screen bg-bg md:grid-cols-[560px_1fr]">
      <aside className="hidden flex-col justify-between bg-panel px-14 py-12 text-panel-text md:flex">
        <Logo size={40} variant="dark" />
        <div className="flex flex-col gap-5">
          <p className="text-display leading-[1.15] font-bold tracking-[-0.02em] whitespace-pre-line">
            {t('login.headline')}
          </p>
          <p className="max-w-[44ch] text-lead leading-relaxed text-panel-text-2">
            {tagline?.trim() ? tagline : t('login.tagline')}
          </p>
        </div>
        <p className="text-secondary text-panel-text-3">© {name}</p>
      </aside>

      <main className="flex flex-col items-center justify-center gap-8 px-4 py-10">
        <Logo size={32} variant="light" className="md:hidden" />
        <div className="w-full max-w-[480px] rounded-2xl border border-line bg-surface p-10 max-sm:p-6">
          <h1 className="text-form font-bold text-ink">{t('login.title')}</h1>
          <p className="mt-1.5 mb-6 text-secondary text-muted">{t('login.subtitle')}</p>
          <LoginForm />
        </div>
      </main>
    </div>
  );
}
