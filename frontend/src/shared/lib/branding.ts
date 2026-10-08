export interface Branding {
  name: string;
  tagline?: string | null;
  accent: string;
  poweredBy: boolean;
  logoUrl: string;
  faviconUrl: string;
}

const DEFAULT_BRANDING: Branding = {
  name: 'Vinette',
  accent: '#8E2043',
  poweredBy: true,
  logoUrl: '/logo.svg',
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

export function applyBranding(): void {
  document.documentElement.style.setProperty('--vn-accent', getBranding().accent);
}
