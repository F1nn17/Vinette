import { useMemo } from 'react';
import { cn } from '@/shared/lib';
import {
  buildFrameDocument,
  DEFAULT_FRAME_COLORS,
  type BodyFormat,
  type FrameColors,
} from './frame-document';

/**
 * What the frame is allowed. Scripts and the frame's own origin are never among them (AGENTS.md section 10): without
 * `allow-scripts` nothing in the mail can run, without `allow-same-origin` it cannot touch this page, its cookies or its
 * storage. Links open in a new tab (`allow-popups`) that is not itself sandboxed, so that the sites behind them work.
 */
export const FRAME_SANDBOX = 'allow-popups allow-popups-to-escape-sandbox';

function readColors(): FrameColors {
  if (typeof document === 'undefined') return DEFAULT_FRAME_COLORS;
  const style = getComputedStyle(document.documentElement);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    text: read('--text-body', DEFAULT_FRAME_COLORS.text),
    link: read('--accent', DEFAULT_FRAME_COLORS.link),
    muted: read('--muted', DEFAULT_FRAME_COLORS.muted),
    border: read('--border', DEFAULT_FRAME_COLORS.border),
  };
}

export interface MessageFrameProps {
  /** The name of the frame for assistive technology, e.g. the subject of the message. */
  title: string;
  /** The body as the server cleaned it. */
  body: string;
  /** `text` bodies get the reading width of DESIGN.md (68ch). */
  format: BodyFormat;
  className?: string;
}

/**
 * Shows the body of a message in a sandboxed frame (the only way mail content is ever shown, AGENTS.md section 10). The
 * content goes in through `srcdoc` with its own CSP. Without scripts and without access to its document the page cannot
 * tell how tall the content is, so the frame fills the space it is given and scrolls inside it.
 */
export function MessageFrame({ title, body, format, className }: MessageFrameProps) {
  const document_ = useMemo(() => buildFrameDocument(body, format, readColors()), [body, format]);
  return (
    <iframe
      title={title}
      sandbox={FRAME_SANDBOX}
      srcDoc={document_}
      referrerPolicy="no-referrer"
      className={cn('block h-full min-h-60 w-full border-0 bg-transparent', className)}
    />
  );
}
