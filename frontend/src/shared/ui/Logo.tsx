import { DEFAULT_LOGO_URL, DEFAULT_NAME, getBranding } from '../lib/branding';
import { cn } from '../lib/cn';

export type LogoVariant = 'light' | 'dark' | 'accent';

/** Default Vinette mark: rounded square, envelope outline whose flap forms a V, seal at the junction. */
export function LogoMark({
  size = 32,
  variant = 'light',
}: {
  size?: number;
  variant?: LogoVariant;
}) {
  const inverted = variant === 'accent';
  const stroke = size <= 32 ? 4.6 : 3.6;
  const line = inverted ? 'stroke-accent' : 'stroke-on-accent';
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden="true" data-testid="logo-mark">
      <rect width="64" height="64" rx="15" className={inverted ? 'fill-surface' : 'fill-accent'} />
      <rect
        x="13"
        y="19"
        width="38"
        height="27"
        rx="4"
        fill="none"
        strokeWidth={stroke}
        className={line}
      />
      <path
        d="M15.5 22.5L32 36l16.5-13.5"
        fill="none"
        strokeWidth={stroke}
        strokeLinecap="round"
        strokeLinejoin="round"
        className={line}
      />
      <circle cx="32" cy="37.5" r="4.6" className={inverted ? 'fill-accent' : 'fill-on-accent'} />
    </svg>
  );
}

const nameColor: Record<LogoVariant, string> = {
  light: 'text-ink',
  dark: 'text-surface',
  accent: 'text-on-accent',
};

export interface LogoProps {
  /** Mark size in px: 32 in the sidebar, 40 on the login panel. */
  size?: number;
  variant?: LogoVariant;
  showName?: boolean;
  className?: string;
}

/** Product mark + name from branding; a custom name without a custom logo shows a letter tile. */
export function Logo({ size = 32, variant = 'light', showName = true, className }: LogoProps) {
  const { name, logoUrl } = getBranding();
  const customLogo = logoUrl !== DEFAULT_LOGO_URL;
  const customName = name !== DEFAULT_NAME;
  const radius = Math.round(size * 0.23);

  let mark;
  if (customLogo) {
    mark = <img src={logoUrl} alt="" width={size} height={size} style={{ borderRadius: radius }} />;
  } else if (customName) {
    mark = (
      <span
        aria-hidden="true"
        data-testid="logo-tile"
        style={{
          width: size,
          height: size,
          borderRadius: radius,
          fontSize: Math.round(size * 0.5),
        }}
        className={cn(
          'inline-flex shrink-0 items-center justify-center font-bold',
          variant === 'accent' ? 'bg-surface text-accent' : 'bg-accent text-on-accent',
        )}
      >
        {Array.from(name.trim())[0]?.toUpperCase()}
      </span>
    );
  } else {
    mark = <LogoMark size={size} variant={variant} />;
  }

  return (
    <span
      className={cn('inline-flex items-center gap-2.5', className)}
      role={showName ? undefined : 'img'}
      aria-label={showName ? undefined : name}
    >
      {mark}
      {showName && (
        <span
          style={{ fontSize: Math.round(size * 0.6) }}
          className={cn('leading-none font-bold tracking-[-0.02em]', nameColor[variant])}
        >
          {name}
        </span>
      )}
    </span>
  );
}
