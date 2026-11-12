/** Colors of the frame's page; read from the design tokens of the application (the frame cannot see them). */
export interface FrameColors {
  text: string;
  link: string;
  muted: string;
  border: string;
}

export const DEFAULT_FRAME_COLORS: FrameColors = {
  text: '#23262d',
  link: '#8e2043',
  muted: '#5f6570',
  border: '#e2e4e9',
};

/**
 * The policy inside the frame, on top of the frame's own sandbox: no scripts at all, no network except pictures that are
 * part of the page (`data:`), styles only inline (a mail styles itself), no forms, no `<base>`.
 */
export const FRAME_CSP = [
  "default-src 'none'",
  "script-src 'none'",
  'img-src data:',
  "style-src 'unsafe-inline'",
  "font-src 'none'",
  "connect-src 'none'",
  "media-src 'none'",
  "object-src 'none'",
  "frame-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
].join('; ');

export type BodyFormat = 'html' | 'text';

/**
 * The whole page shown in the frame: our policy and our base styles (15/1.65, DESIGN.md §5), then the mail's body.
 * `body` must already be cleaned on the server; this is the second layer, not the first.
 */
export function buildFrameDocument(
  body: string,
  format: BodyFormat,
  colors: FrameColors = DEFAULT_FRAME_COLORS,
): string {
  const width = format === 'text' ? 'max-width:68ch;' : '';
  return [
    '<!doctype html><html><head><meta charset="utf-8">',
    `<meta http-equiv="Content-Security-Policy" content="${FRAME_CSP}">`,
    '<meta name="viewport" content="width=device-width,initial-scale=1">',
    '<base target="_blank">',
    `<style>
html{-webkit-text-size-adjust:100%}
body{margin:0;padding:0;color:${colors.text};background:transparent;font:15px/1.65 'Golos Text',system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;overflow-wrap:anywhere;word-break:normal}
.mail{${width}}
a{color:${colors.link}}
img{max-width:100%;height:auto}
table{max-width:100%}
pre{white-space:pre-wrap}
blockquote{margin:0 0 0 4px;padding-left:12px;border-left:3px solid ${colors.border};color:${colors.muted}}
</style>`,
    '</head><body><div class="mail">',
    body,
    '</div></body></html>',
  ].join('');
}
