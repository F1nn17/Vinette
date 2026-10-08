export interface Branding {
  name: string;
  tagline?: string | null;
  accent: string;
  poweredBy: boolean;
  logoUrl: string;
  faviconUrl: string;
}

export const DEFAULT_NAME = 'Vinette';
export const DEFAULT_LOGO_URL = '/icons/vinette.svg';

const DEFAULT_BRANDING: Branding = {
  name: DEFAULT_NAME,
  accent: '#8E2043',
  poweredBy: true,
  logoUrl: DEFAULT_LOGO_URL,
  faviconUrl: '/favicon.svg',
};

/** Branding injected by the server into index.html (see #vinette-config); defaults in dev. */
export function getBranding(): Branding {
  try {
    const el = document.getElementById('vinette-config');
    if (el?.textContent) {
      const parsed = JSON.parse(el.textContent) as { branding?: Partial<Branding> };
      return { ...DEFAULT_BRANDING, ...parsed.branding };
    }
  } catch {
    // fall through to defaults
  }
  return DEFAULT_BRANDING;
}

/** Darkens a #RRGGBB color; used to derive the hover shade of a custom accent. */
export function darken(hex: string, amount = 0.25): string {
  const n = parseInt(hex.slice(1), 16);
  const channel = (shift: number) =>
    Math.round(((n >> shift) & 0xff) * (1 - amount))
      .toString(16)
      .padStart(2, '0');
  return `#${channel(16)}${channel(8)}${channel(0)}`;
}

/** Overrides the accent tokens at runtime (white-label). The default accent keeps the exact spec shades. */
export function applyBranding(): void {
  const { accent } = getBranding();
  const style = document.documentElement.style;
  if (accent.toLowerCase() === DEFAULT_BRANDING.accent.toLowerCase()) return;
  style.setProperty('--accent', accent);
  style.setProperty('--accent-hover', darken(accent));
  style.setProperty('--accent-soft', `${accent}17`);
}
